using System.IO;
using Kudu.Core;
using Kudu.Core.Deployment;
using Microsoft.Extensions.PlatformAbstractions;

namespace Kudu.Services.Web.Services
{
    public class DeploymentEnvironment : IDeploymentEnvironment
    {
        private readonly IEnvironment _environment;

        // CORE TODO Replaced instances of $"{HttpRuntime.AppDomainPath}/bin" with
        // PlatformServices.Default.Application.ApplicationBasePath, and "kudu.exe"
        // with "kudu.dll", since Kudu.Console is now compiled as framework-dependent and needs
        // to be run with dotnet. Can we refactor use of PlatformServices up higher?
        public DeploymentEnvironment(IEnvironment environment)
        {
            _environment = environment;
        }

        public string ExePath
        {
            get
            {
                return Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, "kudu.dll");
            }
        }

        public string ApplicationPath
        {
            get
            {
                return _environment.SiteRootPath;
            }
        }

        public string MSBuildExtensionsPath
        {
            get
            {
                // CORE TODO What is this?
                return Path.Combine(PlatformServices.Default.Application.ApplicationBasePath, "msbuild");
            }
        }
    }
}