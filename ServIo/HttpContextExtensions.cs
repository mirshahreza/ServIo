using Microsoft.AspNetCore.Http;
using PowNet;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Models;
using PowNet.Services;
using System;
using System.Reflection;
using System.Text.Json.Nodes;

namespace ServIo
{
	public static class HttpContextExtensions
	{
		public static UserServerObject ToUserServerObject(this HttpContext context)
		{
			if (!context.Request.Headers.TryGetValue("token", out Microsoft.Extensions.Primitives.StringValues token)) return UserServerObject.NobodyUserServerObject;
			return TokenToUserServerObject(token.ToString());
		}

		public static UserServerObject TokenToUserServerObject(string token)
		{
			if(token.IsNullOrEmpty()) return UserServerObject.NobodyUserServerObject;

			JsonObject u;
			try
			{
                string tokenValue = token.ToStringEmpty();
                if (tokenValue.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) tokenValue = tokenValue.Substring(7);
                u = tokenValue.Decode(PowNetConfiguration.EncryptionSecret).ToJsonObjectByBuiltIn();
			}
			catch(Exception ex)
			{
				//TODO: Log the exception ex for debugging purposes
				return UserServerObject.NobodyUserServerObject;
			}			
			
			string userName = u["UserName"].ToStringEmpty();
			if (userName == "") return UserServerObject.NobodyUserServerObject;
			UserServerObject? user = UserServerObject.FromCache(userName);
			if (user is null || user.IsPerfect == false) user = CreateUserServerObjectFromDb(userName);
			return user;
		}

		public static UserServerObject CreateUserServerObjectFromDb(string userName)
		{
			string? creatorType = PowNetConfiguration.PowNetSection["UserServerObjectCreatorType"].ToStringEmpty();
			string? creatorMethod = PowNetConfiguration.PowNetSection["UserServerObjectCreatorMethod"].ToStringEmpty();

			if (string.IsNullOrEmpty(creatorType))
			{
				throw new InvalidOperationException("UserServerObjectCreatorType is not configured in PowNet settings.");
			}
			if (string.IsNullOrEmpty(creatorMethod))
			{
				throw new InvalidOperationException("UserServerObjectCreatorMethod is not configured in PowNet settings.");
			}

			Type? type = DynamicCodeService.DynaAsm.GetType(creatorType);
			if (type is not null)
			{
				var typeInstance = Activator.CreateInstance(type);
				MethodInfo? m = type.GetMethod(creatorMethod);
				if (m != null)
				{
					UserServerObject? user = (UserServerObject?)m.Invoke(typeInstance, [userName]);
					if (user is null) throw new InvalidOperationException("User activation error!!!");
					user.ToCache();
					return user;
				}
				else
				{
					throw new MissingMethodException($"{creatorMethod} is not exist on {creatorType} class");
				}
			}
			else
			{
				throw new TypeLoadException($"{creatorType} is not exist");
			}
		}

		public static string GetClientIp(this HttpContext context)
		{
			return context.Connection.RemoteIpAddress.MapToIPv4().ToString();
		}
		public static string GetClientAgent(this HttpContext context)
		{
			return context.Request.Headers["User-Agent"].ToString();
		}
		public static bool IsPostFace(this HttpContext context)
		{
			return context.Request.Method == HttpMethods.Post || context.Request.Method == HttpMethods.Put || context.Request.Method == HttpMethods.Patch;
		}

		public static void AddSuccessHeaders(this HttpContext context, long duration, ApiCallInfo appEndWebApiInfo)
		{
			AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status200OK, "Status200OK", "OK");
		}

		public static void AddInternalErrorHeaders(this HttpContext context, long duration, Exception ex, ApiCallInfo appEndWebApiInfo)
		{
			AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status500InternalServerError, "Status500InternalServerError", "Error");
		}

		public static void AddUnauthorizedErrorHeaders(this HttpContext context, long duration, Exception ex, ApiCallInfo appEndWebApiInfo)
		{
			AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status401Unauthorized, "Status401Unauthorized", ex.Message);
		}

		public static void AddNotFoundErrorHeaders(this HttpContext context, long duration, ApiCallInfo appEndWebApiInfo)
		{
			AddAppEndStandardHeaders(context, duration, appEndWebApiInfo, StatusCodes.Status404NotFound, "Status404NotFound", "NOK");
		}

		private static void AddAppEndStandardHeaders(this HttpContext context, long duration, ApiCallInfo appEndWebApiInfo, int statusCode, string statusTitle, string message)
		{
			context.Response.Headers.TryAdd("Server", "AppEnd");
			context.Response.Headers.TryAdd("InstanceName", PowNetConfiguration.PowNetSection["InstanceName"].ToStringEmpty());

			context.Response.Headers.TryAdd("X-Execution-Path", appEndWebApiInfo.RequestPath);
			context.Response.Headers.TryAdd("X-Execution-Controller", appEndWebApiInfo.ControllerName);
			context.Response.Headers.TryAdd("X-Execution-Action", appEndWebApiInfo.ApiName);
			context.Response.Headers.TryAdd("X-Execution-Duration", duration.ToString());
			context.Response.Headers.TryAdd("X-Execution-User", context.User.Identity?.Name);

			context.Response.Headers.TryAdd("X-Result-StatusCode", statusCode.ToString());
			context.Response.Headers.TryAdd("X-Result-StatusTitle", statusTitle);
		}

		public static void AddCacheHeaders(this HttpContext context)
		{
			context.Response.Headers.TryAdd("X-Cache", "HIT");
		}
	}
}
