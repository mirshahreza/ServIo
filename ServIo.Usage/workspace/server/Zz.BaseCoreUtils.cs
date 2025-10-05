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

namespace Zz
{
	/// <summary>
	/// Core foundational utilities kept minimal after refactor.
	/// (Auth / user-facing and admin management methods moved to BaseUserUtils & BaseAdminUtils).
	/// </summary>
	public static class BaseCoreUtils
	{
		public static UserServerObject CreateUserServerObject(string userName)
		{
			DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
			string sql = $"SELECT TOP 1 * FROM BaseUser WHERE UserName='{userName}'"; // TODO: parameterize if extended
			DataTable dt = dbIO.ToDataTable(sql);
			if (dt.Rows.Count == 0) throw new Exception($"User '{userName}' not found.");
			DataRow drUser = dt.Rows[0];
			UserServerObject uso = new() { Id = (int)drUser["Id"], UserName = (string)drUser["UserName"] };

			// Roles
			string rolesSelect = $"SELECT RoleId FROM BaseUserRole WHERE UserId={uso.Id}";
			DataRowCollection drRoles = dbIO.ToDataTable(rolesSelect).Rows;
			List<int> roleIds = [];
			foreach (DataRow drRole in drRoles) roleIds.Add(drRole["RoleId"].ToIntSafe());
			List<Role> roles = GetApplicationRoles();
			uso.Roles = [.. roles.Where(i => roleIds.Contains(i.Id))];
			uso.AllowedActions = [];
			uso.IsPubKey = PowNetConfiguration.PowNetSection["PublicKeyUser"].ToStringEmpty().EqualsIgnoreCase(uso.UserName);
			uso.Data = [];

			// User attributes
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

			// Profile snapshot
			string sqlProfile = $"SELECT Id,CreatedOn,MemberUpdatedOn,GenderId,NationalCode,FirstName,LastName,BirthYear,BirthMonth,BirthDay,Mobile,Picture_FileBody_xs FROM Members WHERE UserId={uso.Id}";
			DataTable dtProfile = dbIO.ToDataTable(sqlProfile);
			if (dtProfile.Rows.Count > 0)
			{
				DataRow drProfile = dtProfile.Rows[0];
				uso.Data["Id"] = drProfile["Id"]?.ToStringEmpty();
				uso.Data["CreatedOn"] = drProfile["CreatedOn"]?.ToString();
				uso.Data["MemberUpdatedOn"] = drProfile["MemberUpdatedOn"]?.ToString();
				uso.Data["GenderId"] = drProfile["GenderId"]?.ToString();
				uso.Data["NationalCode"] = drProfile["NationalCode"]?.ToString();
				uso.Data["FirstName"] = drProfile["FirstName"]?.ToString();
				uso.Data["LastName"] = drProfile["LastName"]?.ToString();
				uso.Data["BirthYear"] = drProfile["BirthYear"]?.ToString();
				uso.Data["BirthMonth"] = drProfile["BirthMonth"]?.ToString();
				uso.Data["BirthDay"] = drProfile["BirthDay"]?.ToString();
				uso.Data["Mobile"] = drProfile["Mobile"]?.ToString();
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
			string sql = "SELECT Id,RoleName,(SELECT AttributeId, ShortName AttributeName, Title FROM BaseRoleAttribute LEFT OUTER JOIN BaseInfo BI ON BI.Id=AttributeId WHERE RoleId=BaseRole.Id FOR JSON AUTO) Attributes FROM BaseRole";
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

