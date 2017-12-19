using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Kudu.Core.Helpers;

namespace Kudu.Services.Web.Pages.DebugConsole
{
    public class DebugConsoleController : Controller
    {
        public ActionResult Index()
        {
            var os = OSDetector.IsOnWindows() ? "Windows" : "Linux";
            return View($"~/Pages/DebugConsole/{os}Console.cshtml");
        }

        public ActionResult LinuxConsole()
        {
            return View($"~/Pages/DebugConsole/LinuxConsole.cshtml");
        }

        public ActionResult WindowsConsole()
        {
            return View($"~/Pages/DebugConsole/WindowsConsole.cshtml");
        }

    }
}