using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Services.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Internal;
using System;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Kudu.Core.Infrastructure;

namespace Kudu.Services.Zip
{
    // Extending VfsControllerBase is a slight abuse since this has nothing to do with vfs. But there is a lot
    // of good reusable logic in there. We could consider extracting a more basic base class from it.
    public class ZipController : VfsControllerBase
    {
        public ZipController(ITracer tracer, IEnvironment environment)
            : base(tracer, environment, environment.RootPath)
        {
        }

        protected override Task<IActionResult> CreateDirectoryGetResponse(DirectoryInfoBase info, string localFilePath)
        {
            if (!Request.Query.TryGetValue("fileName", out var fileName))
            {
                fileName = Path.GetFileName(Path.GetDirectoryName(localFilePath)) + ".zip";
            }

            var result = new FileCallbackResult("application/zip", (outputStream, _) =>
            {
                using (var zip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    foreach (FileSystemInfoBase fileSysInfo in info.GetFileSystemInfos())
                    {
                        var directoryInfo = fileSysInfo as DirectoryInfoBase;
                        if (directoryInfo != null)
                        {
                            zip.AddDirectory(directoryInfo, Tracer, fileSysInfo.Name);
                        }
                        else
                        {
                            // Add it at the root of the zip
                            zip.AddFile(fileSysInfo.FullName, Tracer, String.Empty);
                        }
                    }
                }

                return Task.CompletedTask;
            })
            {
                FileDownloadName = fileName
            };

            return Task.FromResult((IActionResult)result);
        }

        protected override Task<IActionResult> CreateItemGetResponse(FileSystemInfoBase info, string localFilePath)
        {
            // We don't support getting a file from the zip controller
            // Conceivably, it could be a zip file containing just the one file, but that's rarely interesting
            return Task.FromResult((IActionResult)NotFound());
        }

        protected override Task<IActionResult> CreateDirectoryPutResponse(DirectoryInfoBase info, string localFilePath)
        {
            var zipArchive = new ZipArchive(Request.Body, ZipArchiveMode.Read);
            zipArchive.Extract(localFilePath);

            return Task.FromResult((IActionResult)Ok());
        }

        protected override Task<IActionResult> CreateItemPutResponse(FileSystemInfoBase info, string localFilePath, bool itemExists)
        {
            // We don't support putting an individual file using the zip controller
            return Task.FromResult((IActionResult)NotFound());
        }
    }

    // Based on https://blog.stephencleary.com/2016/11/streaming-zip-on-aspnet-core.html
    // (Similar to how the original implementation was based on https://blog.stephencleary.com/2016/10/async-pushstreamcontent.html)
    // Note that a stream wrapper is no longer needed, this was fixed in ZipArchive.
    public class FileCallbackResult : FileResult
    {
        private Func<Stream, ActionContext, Task> _callback;

        public FileCallbackResult(string contentType, Func<Stream, ActionContext, Task> callback)
            : base(contentType)
        {
            _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public override Task ExecuteResultAsync(ActionContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            var executor = new FileCallbackResultExecutor(context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>());
            return executor.ExecuteAsync(context, this);
        }

        private sealed class FileCallbackResultExecutor : FileResultExecutorBase
        {
            public FileCallbackResultExecutor(ILoggerFactory loggerFactory)
                : base(CreateLogger<FileCallbackResultExecutor>(loggerFactory))
            {
            }

            public Task ExecuteAsync(ActionContext context, FileCallbackResult result)
            {
                SetHeadersAndLog(context, result, null);
                return result._callback(context.HttpContext.Response.Body, context);
            }
        }
    }
}
