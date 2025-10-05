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
using System.Text.Json.Nodes;

namespace Zz
{
    /// <summary>
    /// Administrative operations for managing users, roles, attributes and profiles.
    /// </summary>
    public static class BaseAdminUtils
    {
        #region Profile Management
        public static JsonObject GetUserProfile(int userId)
        {
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            var dt = dbIO.ToDataTable($"SELECT TOP 1 Id,UserId,CreatedOn,MemberUpdatedOn,GenderId,NationalCode,FirstName,LastName,BirthYear,BirthMonth,BirthDay,Mobile,Picture_FileBody_xs FROM Members WHERE UserId={userId}");
            JsonObject profile = [];
            if (dt.Rows.Count == 0) return profile;
            var dr = dt.Rows[0];
            void set(string key, Func<object?> getter) { try { profile[key] = getter()?.ToString(); } catch { } }
            set("Id", () => dr["Id"]);
            set("UserId", () => dr["UserId"]);
            set("CreatedOn", () => dr["CreatedOn"]);
            set("MemberUpdatedOn", () => dr["MemberUpdatedOn"]);
            set("GenderId", () => dr["GenderId"]);
            set("NationalCode", () => dr["NationalCode"]);
            set("FirstName", () => dr["FirstName"]);
            set("LastName", () => dr["LastName"]);
            set("BirthYear", () => dr["BirthYear"]);
            set("BirthMonth", () => dr["BirthMonth"]);
            set("BirthDay", () => dr["BirthDay"]);
            set("Mobile", () => dr["Mobile"]);
            set("Picture_FileBody_xs", () => dr["Picture_FileBody_xs"]);
            return profile;
        }

        // Update profile from Person DTO (only patch allowed fields)
        public static JsonObject UpdateUserProfile(int userId, Person update)
        {
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            List<string> sets = new();
            List<DbParameter> parameters = [];
            parameters.Add(dbIO.CreateParameter("UserId", "INT", 0, userId));
            void addNullable<T>(string column, T? value, string sqlType, int size = 0) where T : struct { if (value.HasValue) { sets.Add($"{column}=@{column}"); parameters.Add(dbIO.CreateParameter(column, sqlType, size, value.Value)); } }
            void addRef(string column, string? value, int size = 256) { if (!value.IsNullOrEmpty()) { sets.Add($"{column}=@{column}"); parameters.Add(dbIO.CreateParameter(column, "VARCHAR", size, value)); } }

            // GenderId is non-nullable in Person => treat >0 as update (adjust rule if needed)
            if (update.GenderId > 0) { sets.Add("GenderId=@GenderId"); parameters.Add(dbIO.CreateParameter("GenderId", "INT", 0, update.GenderId)); }
            addRef("NationalCode", update.NationalCode, 64);
            addRef("FirstName", string.IsNullOrWhiteSpace(update.FirstName) ? null : update.FirstName, 128);
            addRef("LastName", string.IsNullOrWhiteSpace(update.LastName) ? null : update.LastName, 128);
            addNullable("BirthYear", update.BirthYear, "INT");
            addNullable("BirthMonth", update.BirthMonth, "TINYINT");
            addNullable("BirthDay", update.BirthDay, "TINYINT");
            addRef("Mobile", string.IsNullOrWhiteSpace(update.Mobile) ? null : update.Mobile, 32);
            if (update.PictureFileBodyXs is not null && update.PictureFileBodyXs.Length > 0)
            {
                sets.Add("Picture_FileBody_xs=@Picture_FileBody_xs");
                parameters.Add(dbIO.CreateParameter("Picture_FileBody_xs", "VARBINARY", update.PictureFileBodyXs.Length, update.PictureFileBodyXs));
            }
            if (update.PictureFileBody is not null && update.PictureFileBody.Length > 0)
            {
                sets.Add("Picture_FileBody=@Picture_FileBody");
                parameters.Add(dbIO.CreateParameter("Picture_FileBody", "VARBINARY", update.PictureFileBody.Length, update.PictureFileBody));
            }

            if (sets.Count == 0) return new JsonObject { ["Success"] = false, ["Message"] = "No fields" };
            string updateSql = $"UPDATE Members SET {string.Join(",", sets)}, MemberUpdatedOn=GETDATE() WHERE UserId=@UserId; SELECT 1 A";
            dbIO.ToDataTable(updateSql, parameters);
            var profile = GetUserProfile(userId);
            profile["Success"] = true;
            return profile;
        }
        #endregion

        #region Roles
        public static JsonObject AssignRoles(int userId, IEnumerable<int> roleIds)
        {
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            var currentRoleIds = dbIO.ToDataTable($"SELECT RoleId FROM BaseUserRole WHERE UserId={userId}").Rows.Cast<DataRow>().Select(r => r["RoleId"].ToIntSafe()).ToHashSet();
            List<int> inserted = new();
            foreach (var rid in roleIds.Distinct())
            {
                if (currentRoleIds.Contains(rid)) continue;
                var prms = new List<DbParameter> { dbIO.CreateParameter("UserId", "INT", 0, userId), dbIO.CreateParameter("RoleId", "INT", 0, rid) };
                dbIO.ToDataTable("INSERT INTO BaseUserRole (UserId,RoleId) VALUES (@UserId,@RoleId); SELECT 1 A", prms);
                inserted.Add(rid);
            }
            return new JsonObject { ["Success"] = true, ["Inserted"] = string.Join(',', inserted) };
        }

        public static JsonObject RemoveRole(int userId, int roleId)
        {
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            var prms = new List<DbParameter> { dbIO.CreateParameter("UserId", "INT", 0, userId), dbIO.CreateParameter("RoleId", "INT", 0, roleId) };
            dbIO.ToDataTable("DELETE FROM BaseUserRole WHERE UserId=@UserId AND RoleId=@RoleId; SELECT 1 A", prms);
            return new JsonObject { ["Success"] = true };
        }
        #endregion

        #region User Attributes
        public static JsonObject SetUserAttribute(int userId, int attributeId)
        {
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            var exists = dbIO.ToDataTable($"SELECT TOP 1 1 FROM BaseUserAttribute WHERE UserId={userId} AND AttributeId={attributeId}");
            if (exists.Rows.Count == 0)
            {
                dbIO.ToDataTable($"INSERT INTO BaseUserAttribute (UserId,AttributeId) VALUES ({userId},{attributeId}); SELECT 1 A");
            }
            return new JsonObject { ["Success"] = true };
        }

        public static JsonObject RemoveUserAttribute(int userId, int attributeId)
        {
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            dbIO.ToDataTable($"DELETE FROM BaseUserAttribute WHERE UserId={userId} AND AttributeId={attributeId}; SELECT 1 A");
            return new JsonObject { ["Success"] = true };
        }
        #endregion
    }
}
