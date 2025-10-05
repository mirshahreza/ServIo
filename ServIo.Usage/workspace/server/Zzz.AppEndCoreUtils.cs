using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using PowNet.Common;
using PowNet.Configuration;
using PowNet.Data;
using PowNet.Extensions;
using PowNet.Models;
using PowNet.Services;
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
	public static class AppEndCoreUtils
	{
		public static UserServerObject CreateUserServerObject(string userName)
		{
			DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
			string sql = $"SELECT TOP 1 * FROM BaseUser WHERE UserName='{userName}'";
			DataTable dt = dbIO.ToDataTable(sql);
			if (dt.Rows.Count == 0) throw new Exception($"User '{userName}' not found.");
			DataRow drUser = dt.Rows[0];
			UserServerObject uso = new() { Id = (int)drUser["Id"], UserName = (string)drUser["UserName"] };
			string rolesSelect = $"SELECT RoleId FROM BaseUserRole WHERE UserId={uso.Id}";
			DataRowCollection drRoles = dbIO.ToDataTable(rolesSelect).Rows;
			List<int> roleIds = [];
			foreach (DataRow drRole in drRoles) roleIds.Add(drRole["RoleId"].ToIntSafe());	
			List<Role> roles = GetApplicationRoles();
			uso.Roles = [.. roles.Where(i => roleIds.Contains( i.Id))];
			uso.AllowedActions = [];
			uso.IsPubKey = PowNetConfiguration.PowNetSection["PublicKeyUser"].ToStringEmpty().EqualsIgnoreCase(uso.UserName);
			uso.Data = [];

			Console.WriteLine($"Creating UserServerObject for user: {uso.UserName} with Id: {uso.Id}");


			// Fetching user attributes
			string sqlAttributes = $"SELECT AttributeId, ShortName AttributeName, Title FROM BaseUserAttribute LEFT OUTER JOIN Common_BaseInfo BI ON BI.Id=AttributeId WHERE UserId={uso.Id}";
			DataTable dtAttributes = dbIO.ToDataTable(sqlAttributes);
			if (dtAttributes.Rows.Count > 0)
			{
				foreach (DataRow dr in dtAttributes.Rows)
				{
					if (dr["AttributeName"] is null || dr["AttributeName"] == DBNull.Value) continue;
					uso.Data[(string)dr["AttributeName"]] = (int)dr["AttributeId"];
					uso.Data[$"{(string)dr["AttributeName"]}-Title"] = (string)dr["Title"];
				}
			}

			// Adding custom properties for demonstration purposes
			string sqlProfile = $"SELECT Id,CreatedOn,MemberUpdatedOn,GenderId,NationalCode,FirstName,LastName,BirthYear,BirthMonth,BirthDay,Mobile,Picture_FileBody_xs FROM Members WHERE UserId={uso.Id}";
			DataTable dtProfile = dbIO.ToDataTable(sqlProfile);
			if (dtProfile.Rows.Count > 0)
			{
				DataRow drProfile = dtProfile.Rows[0];
				uso.Data["Id"] = (string)drProfile["Id"];
				uso.Data["CreatedOn"] = (DateTime)drProfile["CreatedOn"];
				uso.Data["MemberUpdatedOn"] = (DateTime)drProfile["MemberUpdatedOn"];

				uso.Data["GenderId"] = (int)drProfile["GenderId"];
				uso.Data["NationalCode"] = (string)drProfile["NationalCode"];
				uso.Data["FirstName"] = (string)drProfile["FirstName"];
				uso.Data["LastName"] = (string)drProfile["LastName"];

				uso.Data["BirthYear"] = (int)drProfile["BirthYear"];
				uso.Data["BirthMonth"] = (int)drProfile["BirthMonth"];
				uso.Data["BirthDay"] = (int)drProfile["BirthDay"];

				uso.Data["Mobile"] = (int)drProfile["Mobile"];
				uso.Data["Picture_FileBody_xs"] = drProfile["Picture_FileBody_xs"].ToStringEmpty();
			}

			uso.IsPerfect = true;
			return uso;
		}

		public static List<Role> GetApplicationRoles()
		{
			List<Role> roles = MemoryService.SharedMemoryCache.Get<List<Role>>("ApplicationRoles") ?? GetApplicationRolesFromDb();
			return roles;
		}

		public static List<Role> GetApplicationRolesFromDb()
		{
			DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
			string sql = $"SELECT Id,RoleName,(SELECT AttributeId, ShortName AttributeName, Title FROM BaseRoleAttribute LEFT OUTER JOIN BaseInfo BI ON BI.Id=AttributeId WHERE RoleId=BaseRole.Id FOR JSON AUTO) Attributes FROM BaseRole";
			DataRowCollection rowCollection = dbIO.ToDataTable(sql).Rows;
			List<Role> roles = [];
			int pubKeyRoleId = PowNetConfiguration.PowNetSection["PublicKeyRole"].ToIntSafe();
			foreach (DataRow dr in rowCollection) roles.Add(new()
			{
				Id = (int)dr["Id"],
				RoleName = (string)dr["RoleName"],
				IsPubKey = pubKeyRoleId.Equals((int)dr["Id"]),
				Data = ((string)dr["Attributes"]).ToJsonObjectByBuiltIn() ?? []
			});
			return roles;
		}
	}
}

