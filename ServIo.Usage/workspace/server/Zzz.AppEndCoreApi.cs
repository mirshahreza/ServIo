using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using PowNet;
using PowNet.Configuration;
using PowNet.Data;
using PowNet.Extensions;
using PowNet.Models;
using ServIo;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Zzz
{
	[Route("Zzz/AppEndCoreApi")]
	[ApiController]
	public class AppEndCoreApi : ControllerBase
	{
		[HttpPost("Login")]
		public JsonObject Login(string UserName, string Password)
		{
			if (UserName.IsNullOrEmpty()) return new JsonObject { ["Message"] = "Username can not be empty" };
			if (UserName.IsPotentialSqlInjection()) return new JsonObject { ["Message"] = "Username is not valid" }; 
			if (Password.IsNullOrEmpty()) return new JsonObject { ["Message"] = "Password can not be empty" }; 

			DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());

			

			List<DbParameter> dbParameters = [];
			dbParameters.Add(dbIO.CreateParameter("UserName", "VARCHAR", 64, UserName));
			dbParameters.Add(dbIO.CreateParameter("MD5Password", "VARCHAR", 8000, Password.HashMd5()));
            dbParameters.Add(dbIO.CreateParameter("MD4Password", "VARCHAR", 8000, Password.HashMd4()));
            dbParameters.Add(dbIO.CreateParameter("HashedPassword", "VARCHAR", 8000, Password.GetHash()));

            DataTable dt = dbIO.ToDataTable($"SELECT TOP 1 Id FROM BaseUser WHERE UserName=@UserName AND (Password=@MD4Password OR Password=@MD5Password OR Password=@HashedPassword)", dbParameters);

			if (dt.Rows.Count > 0)
			{
				UserServerObject uso = AppEndCoreUtils.CreateUserServerObject(UserName);
				uso.ToCache();
				return new JsonObject
				{
					["Token"] = "bearer " + uso.Tokenize(),
					["UserClientObject"] = uso.ToClientVersion().ToJsonObjectByBuiltIn()
				};
			}
			return new JsonObject { ["Message"] = "Invalid UserName or Password" };
		}

		[HttpPost("GetUserServerObjectByContext")]
		public string GetUserServerObjectByContext()
		{
			UserServerObject uso = AppEndCoreUtils.CreateUserServerObject("admin");
			return uso.ToClientVersion().ToJsonStringByBuiltIn();
		}

		[HttpPost("ReloadCode")]
		public void ReloadCode()
		{
			DynamicServerBootstrap.EnsureDynamicServerScriptsLoaded();
		}

		[HttpPost("PingMe")]
		public string PingMe()
		{
			return "I am at your service.";
		}

		[HttpPost("Echo")]
		public string Echo(string EchoString)
		{
			return EchoString;
		}

		[HttpPost("GetServerDateTime")]
		public DateTime GetServerDateTime()
		{
			return DateTime.Now;
		}
	}
}

