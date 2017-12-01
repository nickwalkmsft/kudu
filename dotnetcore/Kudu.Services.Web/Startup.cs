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
            services.AddMvc();

            var serverConfiguration = new ServerConfiguration();

            // CORE TODO This is new. See if over time we can refactor away the need for this?
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

            // Make sure %HOME% is correctly set
            EnsureHomeEnvironmentVariable();

            IEnvironment environment = GetEnvironment();


            // General
            services.AddSingleton<IServerConfiguration>(serverConfiguration);

            // CORE TODO Looks like this doesn't ever actually do anything, can refactor out?
            services.AddSingleton<IBuildPropertyProvider>(new BuildPropertyProvider());


            // Per request environment
            services.AddScoped<IEnvironment>(sp => GetEnvironment(sp.GetRequiredService<IDeploymentSettingsManager>(), sp.GetRequiredService<IHttpContextAccessor>().HttpContext));

            // CORE TODO We always use a null tracer for now
            var traceFactory = new TracerFactory(() => NullTracer.Instance);
            services.AddSingleton<ITraceFactory>(traceFactory);
            services.AddSingleton<ITracer>(NullTracer.Instance);

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
                { "deployment", _deploymentLock } // DeploymentController, SettingsController, FetchDeploymentManager
            };

            services.AddSingleton<IDictionary<string, IOperationLock>>(namedLocks);

            IDeploymentSettingsManager noContextDeploymentsSettingsManager =
                new DeploymentSettingsManager(new XmlSettings.Settings(GetSettingsPath(environment)));

            var noContextTraceFactory = new TracerFactory(() => GetTracerWithoutContext(environment, noContextDeploymentsSettingsManager));
            var etwTraceFactory = new TracerFactory(() => new ETWTracer(string.Empty, string.Empty));

            // CORE TODO
            // TraceServices.TraceLevel = noContextDeploymentsSettingsManager.GetTraceLevel();

            services.AddTransient<IAnalytics>(sp => new Analytics(sp.GetRequiredService<IDeploymentSettingsManager>(),
                                                                  sp.GetRequiredService<IServerConfiguration>(),
                                                                  noContextTraceFactory));



            services.AddScoped<ISettings>(sp => new XmlSettings.Settings(GetSettingsPath(environment)));

            services.AddScoped<IDeploymentSettingsManager, DeploymentSettingsManager>();

            services.AddScoped<IDeploymentStatusManager, DeploymentStatusManager>();

            services.AddScoped<ISiteBuilderFactory, SiteBuilderFactory>();

            services.AddScoped<IWebHooksManager, WebHooksManager>();

            services.AddScoped<ILogger>(sp => GetLogger());

            services.AddScoped<IDeploymentManager, DeploymentManager>();

            services.AddScoped<IRepositoryFactory>(sp => _deploymentLock.RepositoryFactory = new RepositoryFactory(
                sp.GetRequiredService<IEnvironment>(), sp.GetRequiredService<IDeploymentSettingsManager>(), sp.GetRequiredService<ITraceFactory>()));
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
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

            app.UseStaticFiles();

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller}/{action=Index}/{id?}");

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
            // CORE TODO Not sure how to do this
            //string binPath = HttpRuntime.BinDirectory;
            string binPath = "bin";
            string requestId = httpContext?.Request.GetRequestId();
            string siteRetrictedJwt = httpContext?.Request.GetSiteRetrictedJwt();
            // CORE TODO Environment now requires an HttpContextAccessor, which I have set to null here
            return new Core.Environment(root, EnvironmentHelper.NormalizeBinPath(binPath), repositoryPath, requestId, siteRetrictedJwt, null);
        }

        private static void EnsureHomeEnvironmentVariable()
        {
            // CORE TODO Hard-coding this for now while exploring
            System.Environment.SetEnvironmentVariable("HOME", @"G:\kudu-debug");

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

        // CORE TODO The original GetLogger is below. Do this later. Always get a null logger for now.
        private static ILogger GetLogger() => NullLogger.Instance;

        /*
        private static ILogger GetLogger(IEnvironment environment, IKernel kernel)
        {
            TraceLevel level = kernel.Get<IDeploymentSettingsManager>().GetTraceLevel();
            if (level > TraceLevel.Off && TraceServices.CurrentRequestTraceFile != null)
            {
                string textPath = Path.Combine(environment.DeploymentTracePath, TraceServices.CurrentRequestTraceFile);
                return new TextLogger(textPath);
            }

            return NullLogger.Instance;
        }
        */
    }
}
