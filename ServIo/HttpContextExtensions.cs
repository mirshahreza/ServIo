using Microsoft.AspNetCore.Http;
using PowNet.Abstractions.Authentication;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Implementations.Authentication;
using PowNet.Models;
using PowNet.Services;
using System.Reflection;

namespace ServIo
{
	public static class HttpContextExtensions
	{
		private static readonly ITokenUserResolver _tokenResolver = new TokenUserResolver(new DefaultUserIdentityFactory(), new InMemoryUserCache());

		public static UserServerObject ToUserServerObject(this HttpContext context)
		{
			var identity = _tokenResolver.ResolveFromHttpContext(context);
			return identity as UserServerObject ?? UserServerObject.NobodyUserServerObject;
		}

		public static UserServerObject TokenToUserServerObject(string token)
			=> TokenToUserServerObjectNullable(token);

		public static UserServerObject TokenToUserServerObjectNullable(string? token)
		{
			if (string.IsNullOrWhiteSpace(token)) return UserServerObject.NobodyUserServerObject;
			// normalize bearer prefix
			if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = token[7..];
			var identity = _tokenResolver.ResolveFromToken(token);
			return identity as UserServerObject ?? UserServerObject.NobodyUserServerObject;
		}

		public static UserServerObject CreateUserServerObjectFromDb(string userName)
		{
			string? creatorType = PowNetConfiguration.PowNetSection["UserServerObjectCreatorType"].ToStringEmpty();
			string? creatorMethod = PowNetConfiguration.PowNetSection["UserServerObjectCreatorMethod"].ToStringEmpty();

			if (string.IsNullOrEmpty(creatorType)) throw new InvalidOperationException("UserServerObjectCreatorType is not configured in PowNet settings.");
			if (string.IsNullOrEmpty(creatorMethod)) throw new InvalidOperationException("UserServerObjectCreatorMethod is not configured in PowNet settings.");

			Type? type = DynamicCodeService.DynaAsm.GetType(creatorType);
			if (type is null) throw new TypeLoadException($"{creatorType} is not exist");

			var typeInstance = Activator.CreateInstance(type);
			MethodInfo? m = type.GetMethod(creatorMethod);
			if (m == null) throw new MissingMethodException($"{creatorMethod} is not exist on {creatorType} class");

			UserServerObject? user = (UserServerObject?)m.Invoke(typeInstance, [userName]);
			if (user is null) throw new InvalidOperationException("User activation error!!!");
			user.ToCache();
			return user;
		}

		public static string GetClientIp(this HttpContext context) => context.Connection.RemoteIpAddress.MapToIPv4().ToString();
		public static string GetClientAgent(this HttpContext context) => context.Request.Headers["User-Agent"].ToString();
		public static bool IsPostFace(this HttpContext context) => context.Request.Method == HttpMethods.Post || context.Request.Method == HttpMethods.Put || context.Request.Method == HttpMethods.Patch;

		public static void AddSuccessHeaders(this HttpContext context, long duration, PowNet.Services.ApiCallInfo appEndWebApiInfo) => AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status200OK, "Status200OK");
		public static void AddInternalErrorHeaders(this HttpContext context, long duration, Exception ex, PowNet.Services.ApiCallInfo appEndWebApiInfo) => AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status500InternalServerError, "Status500InternalServerError");
		public static void AddUnauthorizedErrorHeaders(this HttpContext context, long duration, Exception ex, PowNet.Services.ApiCallInfo appEndWebApiInfo) => AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status401Unauthorized, "Status401Unauthorized");
		public static void AddNotFoundErrorHeaders(this HttpContext context, long duration, PowNet.Services.ApiCallInfo appEndWebApiInfo) => AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status404NotFound, "Status404NotFound");

		private static void AddAppEndStandardHeaders(this HttpContext context, long duration, PowNet.Services.ApiCallInfo appEndWebApiInfo, int statusCode, string statusTitle)
		{
			context.Response.Headers["Server"] = "AppEnd";
			context.Response.Headers["InstanceName"] = PowNetConfiguration.PowNetSection["InstanceName"].ToStringEmpty();
			context.Response.Headers["X-Execution-Path"] = appEndWebApiInfo.RequestPath;
			context.Response.Headers["X-Execution-Controller"] = appEndWebApiInfo.ControllerName;
			context.Response.Headers["X-Execution-Action"] = appEndWebApiInfo.ApiName;
			context.Response.Headers["X-Execution-Duration"] = duration.ToString();
			context.Response.Headers["X-Execution-User"] = context.User.Identity?.Name ?? string.Empty;
			context.Response.Headers["X-Result-StatusCode"] = statusCode.ToString();
			context.Response.Headers["X-Result-StatusTitle"] = statusTitle;
		}

		public static void AddCacheHeader(this HttpContext context, string value) => context.Response.Headers["X-Cache"] = value;
	}
}
