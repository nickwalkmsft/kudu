using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Kudu.Contracts.Settings;
using Kudu.Core.Settings;
using XmlSettings;
using Kudu.Core;
using System.IO;
using Microsoft.AspNetCore.Http;
using Kudu.Services.Infrastructure;
using Kudu.Core.Helpers;
using Kudu.Core.Infrastructure;
using Kudu.Contracts.Infrastructure;
using Kudu.Core.Tracing;
using Kudu.Contracts.Tracing;
using Kudu.Services.Web.Infrastructure;
using System.Diagnostics;
using Kudu.Core.Deployment;
using Kudu.Core.Deployment.Generator;
using Kudu.Core.Hooks;
using Kudu.Contracts.SourceControl;
using Kudu.Core.SourceControl;
using Kudu.Services.ServiceHookHandlers;
using Kudu.Services.Deployment;
using Microsoft.Extensions.PlatformAbstractions;
using Kudu.Core.SourceControl.Git;
using Kudu.Services.Web.Services;
using Kudu.Services.GitServer;
using Kudu.Core.Commands;
using Newtonsoft.Json.Serialization;
using Kudu.Services.Web.Tracing;

namespace Kudu.Services.Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // CORE TODO Is this still true?
        // Due to a bug in Ninject we can't use Dispose to clean up LockFile so we shut it down manually
        private static DeploymentLockFile _deploymentLock;

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc()
                .AddJsonOptions(options => options.SerializerSettings.ContractResolver = new DefaultContractResolver());


            var serverConfiguration = new ServerConfiguration();

            // CORE TODO This is new. See if over time we can refactor away the need for this?
            // It's kind of a quick hack/compat shim
            var contextAccessor = new HttpContextAccessor();
            services.AddSingleton<IHttpContextAccessor>(contextAccessor);

            // Make sure %HOME% is correctly set
            EnsureHomeEnvironmentVariable();

            IEnvironment environment = GetEnvironment();

            EnsureDotNetCoreEnvironmentVariable(environment);

            // Add various folders that never change to the process path. All child processes will inherit
            PrependFoldersToPath(environment);

            // Per request environment
            services.AddScoped<IEnvironment>(sp => GetEnvironment(sp.GetRequiredService<IDeploymentSettingsManager>(), sp.GetRequiredService<IHttpContextAccessor>().HttpContext));

            // General
            services.AddSingleton<IServerConfiguration>(serverConfiguration);

            // CORE TODO Looks like this doesn't ever actually do anything, can refactor out?
            services.AddSingleton<IBuildPropertyProvider>(new BuildPropertyProvider());

            /*
             * CORE TODO all this business around ITracerFactory/ITracer/GetTracer()/
             * ILogger needs serious refactoring:
             * - Names should be changed to make it clearer that ILogger is for deployment
             * logging and ITracer and friends are for Kudu tracing
             * - ILogger is a first-class citizen and .NET core. We should be using it (and
             * not name-colliding with it)
             * - ITracer vs. ITraceFactory is redundant and confusing.
             * - All this stuff with funcs and factories and TraceServices is overcomplicated.
             * TraceServices only serves to confuse stuff now that we're avoiding
             * HttpContext.Current
             */

            Func<IServiceProvider, ITracer> resolveTracer = sp => GetTracer(sp);
            Func<ITracer> createTracerThunk = () => resolveTracer(services.BuildServiceProvider());

            // First try to use the current request profiler if any, otherwise create a new one
            var traceFactory = new TracerFactory(() => {
                var sp = services.BuildServiceProvider();
                var context = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;

                return TraceServices.GetRequestTracer(context) ?? resolveTracer(sp);
            });

            services.AddScoped<ITracer>(sp =>
            {
                var context = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
                return TraceServices.GetRequestTracer(context) ?? NullTracer.Instance;
            });

            services.AddSingleton<ITraceFactory>(traceFactory);

            TraceServices.SetTraceFactory(createTracerThunk);

            // Setup the deployment lock
            string lockPath = Path.Combine(environment.SiteRootPath, Constants.LockPath);
            string deploymentLockPath = Path.Combine(lockPath, Constants.DeploymentLockFile);
            string statusLockPath = Path.Combine(lockPath, Constants.StatusLockFile);
            string sshKeyLockPath = Path.Combine(lockPath, Constants.SSHKeyLockFile);
            string hooksLockPath = Path.Combine(lockPath, Constants.HooksLockFile);

            // CORE TODO grabbing traceFactory on the following few lines instead of resolving from the service collection; any difference?
            _deploymentLock = new DeploymentLockFile(deploymentLockPath, traceFactory);
            _deploymentLock.InitializeAsyncLocks();

            var statusLock = new LockFile(statusLockPath, traceFactory);
            var sshKeyLock = new LockFile(sshKeyLockPath, traceFactory);
            var hooksLock = new LockFile(hooksLockPath, traceFactory);

            // CORE TODO This originally used Ninject's "WhenInjectedInto" for specific instances. IServiceCollection
            // doesn't support this concept, or anything similar like named instances. There are a few possibilities, but the hack
            // solution for now is just injecting a dictionary of locks and letting each dependent resolve the one it needs.
            var namedLocks = new Dictionary<string, IOperationLock>
            {
                { "status", statusLock }, // DeploymentStatusManager
                { "ssh", sshKeyLock }, // SSHKeyController
                { "hooks", hooksLock }, // WebHooksManager
                { "deployment", _deploymentLock } // DeploymentController, DeploymentManager, SettingsController, FetchDeploymentManager, LiveScmController, ReceivePackHandlerMiddleware
            };

            services.AddSingleton<IDictionary<string, IOperationLock>>(namedLocks);

            IDeploymentSettingsManager noContextDeploymentsSettingsManager =
                new DeploymentSettingsManager(new XmlSettings.Settings(GetSettingsPath(environment)));

            var noContextTraceFactory = new TracerFactory(() => GetTracerWithoutContext(environment, noContextDeploymentsSettingsManager));
            var etwTraceFactory = new TracerFactory(() => new ETWTracer(string.Empty, string.Empty));

            
            TraceServices.TraceLevel = noContextDeploymentsSettingsManager.GetTraceLevel();

            services.AddTransient<IAnalytics>(sp => new Analytics(sp.GetRequiredService<IDeploymentSettingsManager>(),
                                                                  sp.GetRequiredService<IServerConfiguration>(),
                                                                  noContextTraceFactory));



            services.AddScoped<ISettings>(sp => new XmlSettings.Settings(GetSettingsPath(environment)));

            services.AddScoped<IDeploymentSettingsManager, DeploymentSettingsManager>();

            services.AddScoped<IDeploymentStatusManager, DeploymentStatusManager>();

            services.AddScoped<ISiteBuilderFactory, SiteBuilderFactory>();

            services.AddScoped<IWebHooksManager, WebHooksManager>();

            services.AddScoped<ILogger>(sp => GetLogger(sp));

            services.AddScoped<IDeploymentManager, DeploymentManager>();
            services.AddScoped<IFetchDeploymentManager, FetchDeploymentManager>();

            services.AddScoped<IRepositoryFactory>(sp => _deploymentLock.RepositoryFactory = new RepositoryFactory(
                sp.GetRequiredService<IEnvironment>(), sp.GetRequiredService<IDeploymentSettingsManager>(), sp.GetRequiredService<ITraceFactory>()));
            
            // Git server
            services.AddTransient<IDeploymentEnvironment, DeploymentEnvironment>();

            services.AddScoped<IGitServer>(sp =>
                new GitExeServer(
                    sp.GetRequiredService<IEnvironment>(),
                    _deploymentLock,
                    GetRequestTraceFile(sp),
                    sp.GetRequiredService<IRepositoryFactory>(),
                    sp.GetRequiredService<IDeploymentEnvironment>(),
                    sp.GetRequiredService<IDeploymentSettingsManager>(),
                    sp.GetRequiredService<ITraceFactory>()));

            // Git Servicehook Parsers
            services.AddScoped<IServiceHookHandler, GenericHandler>();
            services.AddScoped<IServiceHookHandler, GitHubHandler>();
            services.AddScoped<IServiceHookHandler, BitbucketHandler>();
            services.AddScoped<IServiceHookHandler, BitbucketHandlerV2>();
            services.AddScoped<IServiceHookHandler, DropboxHandler>();
            services.AddScoped<IServiceHookHandler, CodePlexHandler>();
            services.AddScoped<IServiceHookHandler, CodebaseHqHandler>();
            services.AddScoped<IServiceHookHandler, GitlabHqHandler>();
            services.AddScoped<IServiceHookHandler, GitHubCompatHandler>();
            services.AddScoped<IServiceHookHandler, KilnHgHandler>();
            services.AddScoped<IServiceHookHandler, VSOHandler>();
            services.AddScoped<IServiceHookHandler, OneDriveHandler>();

            services.AddScoped<ICommandExecutor, CommandExecutor>();
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseBrowserLink();
            }
            else
            {
                // CORE TODO
                app.UseExceptionHandler("/Error");
            }

            app.UseTraceMiddleware();
            
            var configuration = app.ApplicationServices.GetRequiredService<IServerConfiguration>();

            // CORE TODO concept of "deprecation" in routes for traces

            // Push url
            foreach (var url in new[] { "/git-receive-pack", $"/{configuration.GitServerRoot}/git-receive-pack" })
            {
                app.Map(url, appBranch => appBranch.RunReceivePackHandler());
            };

            // Fetch hook
            app.Map("/deploy", appBranch => appBranch.RunFetchHandler());

            // Clone url
            foreach (var url in new[] { "/git-upload-pack", $"/{configuration.GitServerRoot}/git-upload-pack" })
            {
                app.Map(url, appBranch => appBranch.RunUploadPackHandler());
            };

            app.UseStaticFiles();

            app.UseMvc(routes =>
            {
                // CORE TODO Default route needed?
                routes.MapRoute(
                    name: "default",
                    template: "{controller}/{action=Index}/{id?}");

                // Git Service
                routes.MapRoute("git-info-refs-root", "info/refs", new { controller = "InfoRefs", action = "Execute" });
                routes.MapRoute("git-info-refs", configuration.GitServerRoot + "/info/refs", new { controller = "InfoRefs", action = "Execute" });

                // Scm (deployment repository)
                routes.MapHttpRouteDual("scm-info", "scm/info", new { controller = "LiveScm", action = "GetRepositoryInfo" });
                routes.MapHttpRouteDual("scm-clean", "scm/clean", new { controller = "LiveScm", action = "Clean" });
                routes.MapHttpRouteDual("scm-delete", "scm", new { controller = "LiveScm", action = "Delete" }, new { verb = new HttpMethodRouteConstraint("DELETE") });

                // Live files editor
                routes.MapHttpRouteDual("vfs-get-files", "vfs/{*path}", new { controller = "Vfs", action = "GetItem" }, new { verb = new HttpMethodRouteConstraint("GET", "HEAD") });
                routes.MapHttpRouteDual("vfs-put-files", "vfs/{*path}", new { controller = "Vfs", action = "PutItem" }, new { verb = new HttpMethodRouteConstraint("PUT") });
                routes.MapHttpRouteDual("vfs-delete-files", "vfs/{*path}", new { controller = "Vfs", action = "DeleteItem" }, new { verb = new HttpMethodRouteConstraint("DELETE") });

                // Zip file handler
                routes.MapHttpRouteDual("zip-get-files", "zip/{*path}", new { controller = "Zip", action = "GetItem" }, new { verb = new HttpMethodRouteConstraint("GET", "HEAD") });
                routes.MapHttpRouteDual("zip-put-files", "zip/{*path}", new { controller = "Zip", action = "PutItem" }, new { verb = new HttpMethodRouteConstraint("PUT") });

                // Zip push deployment
                routes.MapRoute("zip-push-deploy", "api/zipdeploy", new { controller = "PushDeployment", action = "ZipPushDeploy" }, new { verb = new HttpMethodRouteConstraint("POST") });

                // Live Command Line
                routes.MapHttpRouteDual("execute-command", "command", new { controller = "Command", action = "ExecuteCommand" }, new { verb = new HttpMethodRouteConstraint("POST") });

                // Deployments
                routes.MapHttpRouteDual("all-deployments", "deployments", new { controller = "Deployment", action = "GetDeployResults" }, new { verb = new HttpMethodRouteConstraint("GET") });
                routes.MapHttpRouteDual("one-deployment-get", "deployments/{id}", new { controller = "Deployment", action = "GetResult" }, new { verb = new HttpMethodRouteConstraint("GET") });
                routes.MapHttpRouteDual("one-deployment-put", "deployments/{id?}", new { controller = "Deployment", action = "Deploy" }, new { verb = new HttpMethodRouteConstraint("PUT") });
                routes.MapHttpRouteDual("one-deployment-delete", "deployments/{id}", new { controller = "Deployment", action = "Delete" }, new { verb = new HttpMethodRouteConstraint("DELETE") });
                routes.MapHttpRouteDual("one-deployment-log", "deployments/{id}/log", new { controller = "Deployment", action = "GetLogEntry" });
                routes.MapHttpRouteDual("one-deployment-log-details", "deployments/{id}/log/{logId}", new { controller = "Deployment", action = "GetLogEntryDetails" });

                // Settings
                routes.MapHttpRouteDual("set-setting", "settings", new { controller = "Settings", action = "Set" }, new { verb = new HttpMethodRouteConstraint("POST") });
                routes.MapHttpRouteDual("get-all-settings", "settings", new { controller = "Settings", action = "GetAll" }, new { verb = new HttpMethodRouteConstraint("GET") });
                routes.MapHttpRouteDual("get-setting", "settings/{key}", new { controller = "Settings", action = "Get" }, new { verb = new HttpMethodRouteConstraint("GET") });
                routes.MapHttpRouteDual("delete-setting", "settings/{key}", new { controller = "Settings", action = "Delete" }, new { verb = new HttpMethodRouteConstraint("DELETE") });
            });
        }

        private static string GetSettingsPath(IEnvironment environment)
        {
            return Path.Combine(environment.DeploymentsPath, Constants.DeploySettingsPath);
        }

        private static IEnvironment GetEnvironment(IDeploymentSettingsManager settings = null, HttpContext httpContext = null)
        {
            string root = PathResolver.ResolveRootPath();
            string siteRoot = Path.Combine(root, Constants.SiteFolder);
            string repositoryPath = Path.Combine(siteRoot, settings == null ? Constants.RepositoryPath : settings.GetRepositoryPath());
            // CORE TODO see if we can refactor out PlatformServices as high up as we can?
            string binPath = PlatformServices.Default.Application.ApplicationBasePath;
            string requestId = httpContext?.Request.GetRequestId();
            string siteRetrictedJwt = httpContext?.Request.GetSiteRetrictedJwt();
            // CORE TODO Environment now requires an HttpContextAccessor, which I have set to null here
            return new Core.Environment(root, EnvironmentHelper.NormalizeBinPath(binPath), repositoryPath, requestId, siteRetrictedJwt, null);
        }

        private static void EnsureHomeEnvironmentVariable()
        {
            // CORE TODO Hard-coding this for now while exploring. Have a look at what
            // PlatformServices.Default and the injected IHostingEnvironment have at runtime.
            if (!Directory.Exists(System.Environment.ExpandEnvironmentVariables(@"%HOME%")))
            {
                if (OSDetector.IsOnWindows())
                {
                    System.Environment.SetEnvironmentVariable("HOME", @"G:\kudu-debug");
                }
                else
                {
                    System.Environment.SetEnvironmentVariable("HOME", "/home");
                }
            }

            /*
            // If MapPath("/_app") returns a valid folder, set %HOME% to that, regardless of
            // it current value. This is the non-Azure code path.
            string path = HostingEnvironment.MapPath(Constants.MappedSite);
            if (Directory.Exists(path))
            {
                path = Path.GetFullPath(path);
                System.Environment.SetEnvironmentVariable("HOME", path);
            }
            */
        }

        private static ITracer GetTracer(IServiceProvider serviceProvider)
        {
            IEnvironment environment = serviceProvider.GetRequiredService<IEnvironment>();
            TraceLevel level = serviceProvider.GetRequiredService<IDeploymentSettingsManager>().GetTraceLevel();
            var contextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
            var httpContext = contextAccessor.HttpContext;
            var requestTraceFile = TraceServices.GetRequestTraceFile(httpContext);
            if (level > TraceLevel.Off && requestTraceFile != null)
            {
                string textPath = Path.Combine(environment.TracePath, requestTraceFile);
                return new CascadeTracer(new XmlTracer(environment.TracePath, level), new TextTracer(textPath, level), new ETWTracer(environment.RequestId, TraceServices.GetHttpMethod(httpContext)));
            }

            return NullTracer.Instance;
        }

        private static ITracer GetTracerWithoutContext(IEnvironment environment, IDeploymentSettingsManager settings)
        {
            // when file system has issue, this can throw (environment.TracePath calls EnsureDirectory).
            // prefer no-op tracer over outage.
            return OperationManager.SafeExecute(() =>
            {
                TraceLevel level = settings.GetTraceLevel();
                if (level > TraceLevel.Off)
                {
                    return new XmlTracer(environment.TracePath, level);
                }

                return NullTracer.Instance;
            }) ?? NullTracer.Instance;
        }

        private static ILogger GetLogger(IServiceProvider serviceProvider)
        {
            IEnvironment environment = serviceProvider.GetRequiredService<IEnvironment>();
            TraceLevel level = serviceProvider.GetRequiredService<IDeploymentSettingsManager>().GetTraceLevel();
            var contextAccessor = serviceProvider.GetRequiredService<IHttpContextAccessor>();
            var httpContext = contextAccessor.HttpContext;
            var requestTraceFile = TraceServices.GetRequestTraceFile(httpContext);
            if (level > TraceLevel.Off && requestTraceFile != null)
            {
                string textPath = Path.Combine(environment.DeploymentTracePath, requestTraceFile);
                return new TextLogger(textPath);
            }

            return NullLogger.Instance;
        }

        private static void PrependFoldersToPath(IEnvironment environment)
        {
            List<string> folders = PathUtilityFactory.Instance.GetPathFolders(environment);

            string path = System.Environment.GetEnvironmentVariable("PATH");
            string additionalPaths = String.Join(Path.PathSeparator.ToString(), folders);

            // Make sure we haven't already added them. This can happen if the Kudu appdomain restart (since it's still same process)
            if (!path.Contains(additionalPaths))
            {
                path = additionalPaths + Path.PathSeparator + path;

                // PHP 7 was mistakenly added to the path unconditionally on Azure. To work around, if we detect
                // some PHP v5.x anywhere on the path, we yank the unwanted PHP 7
                // TODO: remove once the issue is fixed on Azure
                if (path.Contains(@"PHP\v5"))
                {
                    path = path.Replace(@"D:\Program Files (x86)\PHP\v7.0" + Path.PathSeparator, String.Empty);
                }

                System.Environment.SetEnvironmentVariable("PATH", path);
            }
        }

        private static void EnsureDotNetCoreEnvironmentVariable(IEnvironment environment)
        {
            // Skip this as it causes huge files to be downloaded to the temp folder
            SetEnvironmentVariableIfNotYetSet("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "true");

            // Don't download xml comments, as they're large and provide no benefits outside of a dev machine
            SetEnvironmentVariableIfNotYetSet("NUGET_XMLDOC_MODE", "skip");

            if (Core.Environment.IsAzureEnvironment())
            {
                // On Azure, restore nuget packages to d:\home\.nuget so they're persistent. It also helps
                // work around https://github.com/projectkudu/kudu/issues/2056.
                // Note that this only applies to project.json scenarios (not packages.config)
                SetEnvironmentVariableIfNotYetSet("NUGET_PACKAGES", Path.Combine(environment.RootPath, ".nuget"));

                // Set the telemetry environment variable
                SetEnvironmentVariableIfNotYetSet("DOTNET_CLI_TELEMETRY_PROFILE", "AzureKudu");
            }
            else
            {
                // Set it slightly differently if outside of Azure to differentiate
                SetEnvironmentVariableIfNotYetSet("DOTNET_CLI_TELEMETRY_PROFILE", "Kudu");
            }
        }

        private static void SetEnvironmentVariableIfNotYetSet(string name, string value)
        {
            if (System.Environment.GetEnvironmentVariable(name) == null)
            {
                System.Environment.SetEnvironmentVariable(name, value);
            }
        }
        private static string GetRequestTraceFile(IServiceProvider serviceProvider)
        {
            TraceLevel level = serviceProvider.GetRequiredService<IDeploymentSettingsManager>().GetTraceLevel();
            // CORE TODO Need TraceServices implementation
            //if (level > TraceLevel.Off)
            //{
            //    return TraceServices.CurrentRequestTraceFile;
            //}

            return null;
        }
    }
}
