using Microsoft.AspNetCore.Http;
using PowNet.Abstractions.Api;
using PowNet.Abstractions.Authentication;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Implementations.Api;
using PowNet.Implementations.Authentication;
using PowNet.Logging;
using PowNet.Models;
using PowNet.Services;
using ServIo.Implementations;
using System.Diagnostics;
using System.Text;

namespace ServIo
{
	public class ApiGatewayMiddleware
	{
		private readonly RequestDelegate _next;
		private readonly IApiCallParser _parser;
		private readonly IApiAuthorizationService _auth;
		private readonly IApiCacheService _cache;
		private readonly IActivityLogger _activity;
		private readonly ITokenUserResolver _userResolver;
		private static readonly Logger _log = PowNetLogger.GetLogger("ApiGateway");

		public ApiGatewayMiddleware(RequestDelegate next,
			IApiCallParser? parser = null,
			IApiAuthorizationService? auth = null,
			IApiCacheService? cache = null,
			IActivityLogger? activity = null,
			ITokenUserResolver? userResolver = null)
		{
			_next = next;
			_parser = parser ?? new ApiCallParserAdapter();
			_auth = auth ?? new ApiAuthorizationService();
			_cache = cache ?? new SizedApiCacheService();
			_activity = activity ?? new ActivityLogger();
			_userResolver = userResolver ?? new TokenUserResolver(new DefaultUserIdentityFactory(), new InMemoryUserCache());
		}

		public async Task InvokeAsync(HttpContext context)
		{
			if (!context.IsPostFace())
			{
				await _next(context);
				return;
			}

			Stopwatch sw = Stopwatch.StartNew();
			bool success = true;
			string message = string.Empty;
			string rowId = string.Empty;

			IApiCallInfo call = _parser.Parse(context);
			var apiCallInfo = call as PowNet.Services.ApiCallInfo ?? context.GetApiCallInfo();
			var controllerConf = apiCallInfo.GetConfig();
			var apiConf = controllerConf.ApiConfigurations.SingleOrDefault(i => i.ApiName == apiCallInfo.ApiName) ?? new ApiConfiguration() { ApiName = apiCallInfo.ApiName };

			IUserIdentity user = _userResolver.ResolveFromHttpContext(context);

			if (context.IsPostFace()) context.Request.EnableBuffering();

			string? cacheKey = null;

			try
			{
				if (string.IsNullOrEmpty(call.Controller) || string.IsNullOrEmpty(call.Action))
					throw new EndPointNotFoundException($"Not found resource : {context.Request.Path}");

				// Local rule evaluation to honor full ApiConfiguration (DeniedUsers, etc.)
				if (!EvaluateAccessRules(user, apiConf))
					throw new UnauthorizedAccessException($"Access denied to the {call.Namespace}.{call.Controller}.{call.Action}.");

				// Fallback to generic auth (still executes for consistency / future expansion)
				if (!_auth.HasAccess(user, new ApiConfigurationAdapter(apiConf)))
					throw new UnauthorizedAccessException($"Access denied to the {call.Namespace}.{call.Controller}.{call.Action}.");

				if (apiConf.IsCachingEnabled())
				{
					cacheKey = BuildCacheKey(call, user, apiConf);
					if (_cache.TryGet(cacheKey, out var cached) && cached is not null)
					{
						sw.Stop();
						context.Response.StatusCode = StatusCodes.Status200OK;
						context.Response.ContentType = cached.ContentType;
						context.AddCacheHeader("HIT");
						context.AddSuccessHeaders(sw.ElapsedMilliseconds, apiCallInfo);
						await context.Response.WriteAsync(cached.Content, Encoding.UTF8);
						rowId = context.Items["RowId"]?.ToString() ?? string.Empty;
						if (apiConf.IsLoggingEnabled()) _activity.LogActivity(context, user, call, rowId, success, message, sw.ElapsedMilliseconds.ToIntSafe());
						return;
					}
				}

				MemoryStream? captureStream = null;
				Stream? originalBody = null;
				bool capture = apiConf.IsCachingEnabled();
				if (capture)
				{
					originalBody = context.Response.Body;
					captureStream = new MemoryStream();
					context.Response.Body = captureStream;
				}

				context.Response.OnStarting(() =>
				{
					context.Response.StatusCode = StatusCodes.Status200OK;
					context.AddSuccessHeaders(sw.ElapsedMilliseconds, apiCallInfo);
					return Task.CompletedTask;
				});

				await _next(context);

				if (capture && captureStream != null && originalBody != null && cacheKey != null)
				{
					await CaptureAndCacheAsync(context, captureStream, originalBody, cacheKey, apiConf);
				}
			}
			catch (EndPointNotFoundException ex)
			{
				success = false; message = ex.Message;
				await HandleNotFound(context, apiCallInfo, sw.ElapsedMilliseconds);
			}
			catch (UnauthorizedAccessException ex)
			{
				success = false; message = ex.Message;
				await HandleUnauthorized(context, apiCallInfo, sw.ElapsedMilliseconds, ex);
			}
			catch (Exception ex)
			{
				success = false; message = ex.Message;
				await HandleError(context, apiCallInfo, sw.ElapsedMilliseconds, ex);
			}
			finally
			{
				sw.Stop();
				rowId = context.Items["RowId"]?.ToString() ?? string.Empty;
				if (apiConf.IsLoggingEnabled()) _activity.LogActivity(context, user, call, rowId, success, message, sw.ElapsedMilliseconds.ToIntSafe());
			}
		}

		private static string BuildCacheKey(IApiCallInfo call, IUserIdentity user, ApiConfiguration apiConf)
		{
			bool perUser = apiConf.CacheLevel == PowNet.Common.CacheLevel.PerUser;
			return $"Response::{call.Controller}_{call.Action}{(perUser ? "_" + user.UserName : string.Empty)}";
		}

		private static bool EvaluateAccessRules(IUserIdentity identity, ApiConfiguration apiConf)
		{
			if (identity is not UserServerObject uso) return !identity.IsAnonymous; // fallback
			// Deny rules first
			if (apiConf.DeniedUsers?.Contains(uso.Id) == true) return false;
			if (apiConf.DeniedRoles?.Count > 0 && uso.Roles?.Count > 0 && apiConf.DeniedRoles.HasIntersect(uso.Roles.Select(r => r.Id).ToList())) return false;
			// Allow rules
			if (apiConf.AllowedUsers?.Contains(uso.Id) == true) return true;
			if (apiConf.AllowedRoles?.HasIntersect(uso.Roles.Select(r => r.Id).ToList()) == true) return true;
			// Open rules
			if (apiConf.CheckAccessLevel == PowNet.Common.CheckAccessLevel.OpenForAllUsers) return true;
			if (apiConf.CheckAccessLevel == PowNet.Common.CheckAccessLevel.OpenForAuthenticatedUsers && !uso.IsAnonymous) return true;
			// Default: if no explicit allow lists specified -> deny (secure by default)
			return false;
		}

		private async Task CaptureAndCacheAsync(HttpContext context, MemoryStream captureStream, Stream originalBody, string cacheKey, ApiConfiguration apiConf)
		{
			try
			{
				captureStream.Position = 0;
				if (context.Response.StatusCode == StatusCodes.Status200OK)
				{
					string body = await new StreamReader(captureStream, Encoding.UTF8, leaveOpen: true).ReadToEndAsync();
					captureStream.Position = 0;
					_cache.Set(cacheKey, new PowNet.Abstractions.Api.CachedApiResponse(body, context.Response.ContentType), TimeSpan.FromSeconds(apiConf.CacheSeconds));
					context.AddCacheHeader("MISS");
				}
				await captureStream.CopyToAsync(originalBody);
			}
			finally
			{
				context.Response.Body = originalBody;
				captureStream.Dispose();
			}
		}

		private async Task HandleError(HttpContext context, PowNet.Services.ApiCallInfo call, long dur, Exception ex)
		{
			_log.LogError("{Error} : {Path}", ex.Message, context.Request.Path);
			context.Response.StatusCode = StatusCodes.Status500InternalServerError;
			context.AddInternalErrorHeaders(dur, ex, call);
			context.Response.ContentType = "text/HTML";
			await context.Response.WriteAsync($"Message : {ex.Message + Environment.NewLine + ex.StackTrace}");
		}
		private async Task HandleUnauthorized(HttpContext context, PowNet.Services.ApiCallInfo call, long dur, UnauthorizedAccessException ex)
		{
			_log.LogError("{Error} : {Path}", ex.Message, context.Request.Path);
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			context.AddUnauthorizedErrorHeaders(dur, ex, call);
			context.Response.ContentType = "text/HTML";
			await context.Response.WriteAsync(ex.Message + Environment.NewLine + ex.StackTrace);
		}
		private async Task HandleNotFound(HttpContext context, PowNet.Services.ApiCallInfo call, long dur)
		{
			_log.LogWarning("Not found resource : {Path}", context.Request.Path);
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			context.AddNotFoundErrorHeaders(dur, call);
			context.Response.ContentType = "text/HTML";
			await context.Response.WriteAsync(string.Empty);
		}

		private sealed class ApiCallParserAdapter : IApiCallParser
		{
			public IApiCallInfo Parse(HttpContext httpContext) => httpContext.GetApiCallInfo();
		}

		private sealed class ApiConfigurationAdapter : IApiConfiguration
		{
			private readonly ApiConfiguration _inner;
			public ApiConfigurationAdapter(ApiConfiguration inner) => _inner = inner;
			public string ApiName => _inner.ApiName;
			public bool CachingEnabled => _inner.IsCachingEnabled();
			public bool LoggingEnabled => _inner.IsLoggingEnabled();
			public TimeSpan? AbsoluteCacheDuration => _inner.IsCachingEnabled() ? TimeSpan.FromSeconds(_inner.CacheSeconds) : null;
		}
	}

	public class EndPointNotFoundException(string message) : Exception(message);
}
