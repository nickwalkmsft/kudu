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

            // Make sure %HOME% is correctly set
            EnsureHomeEnvironmentVariable();

            IEnvironment environment = GetEnvironment();

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

            // CORE TODO grabbing traceFactory here instead of resolving from the service collection; any difference?
            _deploymentLock = new DeploymentLockFile(deploymentLockPath, traceFactory);
            _deploymentLock.InitializeAsyncLocks();

            // CORE TODO Ninject's "WhenInjectedInto" for specific instances

            services.AddSingleton<IOperationLock>(_deploymentLock);

            services.AddScoped<ISettings>(sp => new XmlSettings.Settings(GetSettingsPath(environment)));

            services.AddScoped<IDeploymentSettingsManager, DeploymentSettingsManager>();
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
    }
}
