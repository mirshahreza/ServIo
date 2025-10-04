using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Models;
using PowNet.Services;
using System.Diagnostics;
using System.Text;

namespace ServIo
{
	public class ApiGatewayMiddleware(RequestDelegate next)
	{
		private readonly RequestDelegate _next = next;
		
		public async Task InvokeAsync(HttpContext context)
		{
			if (!context.IsPostFace())
			{
				await _next(context);
				return;
			}

			Stopwatch sw = Stopwatch.StartNew();
			bool result = true;
			string message = "";
			string rowId = "";

			ApiCallInfo apiCalling = context.GetAppEndWebApiInfo();
            ControllerConfiguration controllerConf = apiCalling.GetConfig();
			ApiConfiguration apiConf = controllerConf.ApiConfigurations.SingleOrDefault(i => i.ApiName == apiCalling.ApiName) ?? new ApiConfiguration() { ApiName = apiCalling.ApiName };
			UserServerObject actor = context.ToUserServerObject();

			if (context.IsPostFace()) context.Request.EnableBuffering();

			if (string.IsNullOrEmpty(apiCalling.ControllerName) || string.IsNullOrEmpty(apiCalling.ApiName))
			{
				sw.Stop();
				await HandleNotFoundResource(context, apiCalling, sw.ElapsedMilliseconds);
				return;
			}

			try
			{
				if (!actor.HasAccess(apiConf)) throw new UnauthorizedAccessException($"Access denied to the {apiCalling.NamespaceName}.{apiCalling.ControllerName}.{apiCalling.ApiName}.");

				string? cacheKey = null;
				if (apiConf.IsCachingEnabled())
				{
					cacheKey = apiCalling.GetCacheKey(apiConf, actor);
					if (MemoryService.SharedMemoryCache.TryGetValue(cacheKey, out CacheObject? cacheObject))
					{
						// Cache HIT
						sw.Stop();
						context.Response.StatusCode = StatusCodes.Status200OK;
						context.Response.ContentType = cacheObject.ContentType;
						context.AddCacheHeaders();
						context.Response.Headers["X-Cache"] = "HIT";
						context.AddSuccessHeaders(sw.ElapsedMilliseconds, apiCalling);
						await context.Response.WriteAsync(cacheObject.Content, Encoding.UTF8);
						rowId = context.Items["RowId"]?.ToString() ?? "";
						return;
					}
				}

				// Cache MISS path: capture response body so we can store it after pipeline executes.
				MemoryStream? captureStream = null;
				Stream? originalBody = null;
				if (apiConf.IsCachingEnabled())
				{
					originalBody = context.Response.Body;
					captureStream = new MemoryStream();
					context.Response.Body = captureStream;
				}

				context.Response.OnStarting(() =>
				{
					context.Response.StatusCode = StatusCodes.Status200OK;
					context.AddSuccessHeaders(sw.ElapsedMilliseconds, apiCalling);
					return Task.CompletedTask;
				});

				try
				{
					await _next(context);
				}
				finally
				{
					// Finalize caching after response is generated
					if (captureStream is not null && originalBody is not null)
					{
						try
						{
							captureStream.Position = 0;
							if (context.Response.StatusCode == StatusCodes.Status200OK)
							{
								string bodyText = await new StreamReader(captureStream, Encoding.UTF8, leaveOpen: true).ReadToEndAsync();
								captureStream.Position = 0; // reset for copy back
								if (apiConf.IsCachingEnabled())
								{
									CacheObject cacheObject = new()
									{
										Content = bodyText,
										ContentType = context.Response.ContentType
									};
									MemoryService.SharedMemoryCache.Set(cacheKey!, cacheObject, apiConf.GetCacheOptions());
									context.AddCacheHeaders();
									context.Response.Headers["X-Cache"] = "MISS";
								}
							}
							// Copy the (possibly cached) content back to the original stream
							await captureStream.CopyToAsync(originalBody);
							context.Response.Body = originalBody;
						}
						finally
						{
							captureStream?.Dispose();
						}
					}
				}
			}
			catch (UnauthorizedAccessException ex)
			{
				sw.Stop();
				result = false;
				message = ex.Message;
				rowId = context.Items["RowId"]?.ToString() ?? "";
				await HandleUnauthorizedException(context, apiCalling, sw.ElapsedMilliseconds, ex);
			}
			catch (Exception ex)
			{
				sw.Stop();
				result = false;
				message = ex.Message;
				rowId = context.Items["RowId"]?.ToString() ?? "";
				await HandleException(context, apiCalling, sw.ElapsedMilliseconds, ex);
			}
			finally
			{
				sw.Stop();
				rowId = context.Items["RowId"]?.ToString() ?? "";
				if (apiConf.IsLoggingEnabled()) LogManager.LogActivity(context, actor, apiCalling, rowId, result, message, sw.ElapsedMilliseconds.ToIntSafe());
			}
		}

		private async Task HandleException(HttpContext context, ApiCallInfo apiCalling, long duration, Exception ex)
		{
			LogManager.LogError($"{ex.Message} : {context.Request.Path}");
			context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			context.AddInternalErrorHeaders(duration, ex, apiCalling);
			context.Response.ContentType = "text/HTML";
			await context.Response.WriteAsync($"Message : {ex.Message + Environment.NewLine + ex.StackTrace}");
		}
		private async Task HandleUnauthorizedException(HttpContext context, ApiCallInfo apiCalling, long duration, UnauthorizedAccessException ex)
		{
			LogManager.LogError($"{ex.Message} : {context.Request.Path}");
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.AddUnauthorizedErrorHeaders(duration, ex, apiCalling);
			context.Response.ContentType = "text/HTML";
			await context.Response.WriteAsync(ex.Message + Environment.NewLine + ex.StackTrace);
		}
		private async Task HandleNotFoundResource(HttpContext context, ApiCallInfo apiCalling, long duration)
		{
			LogManager.LogError($"Not found resource : {context.Request.Path}");
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			context.AddNotFoundErrorHeaders(duration, apiCalling);
			context.Response.ContentType = "text/HTML";
			await context.Response.WriteAsync("");
		}

		// Local cache DTO (fallback if framework one not available)
		private sealed class CacheObject
		{
			public string Content { get; set; } = string.Empty;
			public string? ContentType { get; set; }
		}
	}
}
