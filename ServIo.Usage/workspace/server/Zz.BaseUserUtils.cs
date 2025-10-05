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
    /// End-user oriented operations: login, registration, password management, profile view/update (self scope).
    /// </summary>
    public static class BaseUserUtils
    {
        #region Cache helpers (wrapping MemoryService custom cache API)
        private static T? GetCache<T>(string key)
        {
            MemoryService.SharedMemoryCache.TryGetValue(key, out var val);
            return val is T tv ? tv : default;
        }
        private static void SetCache(string key, object value, TimeSpan ttl)
        {
            MemoryService.SharedMemoryCache.TryAdd(key, value, ttl);
        }
        private static void RemoveCache(string key)
        {
            MemoryService.SharedMemoryCache.TryRemove(key);
        }
        #endregion

        #region Login / Auth
        private static string AttemptsKey(string userName) => $"login:attempts:{userName.ToLowerInvariant()}";
        private static string LockoutKey(string userName) => $"login:lockout:{userName.ToLowerInvariant()}";

        public static bool IsUserLockedOut(string userName, out TimeSpan? remaining)
        {
            remaining = null;
            var until = GetCache<DateTime?>(LockoutKey(userName));
            if (until is null) return false;
            if (DateTime.UtcNow >= until.Value)
            {
                RemoveCache(LockoutKey(userName));
                return false;
            }
            remaining = until.Value - DateTime.UtcNow;
            return true;
        }

        private static void RegisterLoginFailure(string userName)
        {
            int attempts = GetCache<int?>(AttemptsKey(userName)) ?? 0;
            attempts++;
            SetCache(AttemptsKey(userName), attempts, TimeSpan.FromMinutes(PowNetConfiguration.LockoutDurationMinutes));
            if (attempts >= PowNetConfiguration.MaxLoginAttempts)
            {
                SetCache(LockoutKey(userName), DateTime.UtcNow.AddMinutes(PowNetConfiguration.LockoutDurationMinutes), TimeSpan.FromMinutes(PowNetConfiguration.LockoutDurationMinutes));
            }
        }

        private static void RegisterLoginSuccess(string userName)
        {
            RemoveCache(AttemptsKey(userName));
            RemoveCache(LockoutKey(userName));
        }

        public static (UserServerObject? uso, string? message) Login(string userName, string password)
        {
            if (userName.IsNullOrEmpty() || password.IsNullOrEmpty()) return (null, "Username/Password required");
            if (IsUserLockedOut(userName, out var rem)) return (null, $"Locked {rem?.TotalMinutes:F0}m");

            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            List<DbParameter> dbParameters = [];
            dbParameters.Add(dbIO.CreateParameter("UserName", "VARCHAR", 64, userName));
            dbParameters.Add(dbIO.CreateParameter("MD5Password", "VARCHAR", 8000, password.HashMd5()));
            dbParameters.Add(dbIO.CreateParameter("MD4Password", "VARCHAR", 8000, password.HashMd4()));
            dbParameters.Add(dbIO.CreateParameter("HashedPassword", "VARCHAR", 8000, password.GetHash()));

            DataTable dt = dbIO.ToDataTable("SELECT TOP 1 Id FROM BaseUser WHERE UserName=@UserName AND (Password=@MD4Password OR Password=@MD5Password OR Password=@HashedPassword)", dbParameters);
            if (dt.Rows.Count == 0)
            {
                RegisterLoginFailure(userName);
                return (null, "Invalid credentials");
            }
            RegisterLoginSuccess(userName);
            var uso = BaseCoreUtils.CreateUserServerObject(userName);
            uso.ToCache();
            return (uso, null);
        }
        #endregion

        #region Registration / Password Reset (basic skeletons)
        public static (bool success, string message) Register(string userName, string password)
        {
            if (userName.IsNullOrEmpty() || password.IsNullOrEmpty()) return (false, "Username/password required");
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            var safeUser = userName.Replace("'", "''");
            var dt = dbIO.ToDataTable($"SELECT TOP 1 1 A FROM BaseUser WHERE UserName='{safeUser}'");
            if (dt.Rows.Count > 0) return (false, "Username exists");
            var hashed = password.GetHash();
            var prms = new List<DbParameter> { dbIO.CreateParameter("UserName", "VARCHAR", 64, userName), dbIO.CreateParameter("Password", "VARCHAR", 8000, hashed) };
            dbIO.ToDataTable("INSERT INTO BaseUser (UserName,Password) VALUES (@UserName,@Password); SELECT SCOPE_IDENTITY() Id", prms);
            return (true, "Registered");
        }

        public static (bool success, string message) ChangePassword(int userId, string currentPassword, string newPassword)
        {
            if (newPassword.ValidatePasswordStrength().IsValid == false) return (false, "Weak password");
            DbIO dbIO = DbIO.Instance(DatabaseConfiguration.FromSettings());
            List<DbParameter> p = [];
            p.Add(dbIO.CreateParameter("UserId", "INT", 0, userId));
            p.Add(dbIO.CreateParameter("MD5Password", "VARCHAR", 8000, currentPassword.HashMd5()));
            p.Add(dbIO.CreateParameter("MD4Password", "VARCHAR", 8000, currentPassword.HashMd4()));
            p.Add(dbIO.CreateParameter("HashedPassword", "VARCHAR", 8000, currentPassword.GetHash()));
            var dt = dbIO.ToDataTable("SELECT TOP 1 Id FROM BaseUser WHERE Id=@UserId AND (Password=@MD4Password OR Password=@MD5Password OR Password=@HashedPassword)", p);
            if (dt.Rows.Count == 0) return (false, "Current password invalid");
            List<DbParameter> up = [];
            up.Add(dbIO.CreateParameter("UserId", "INT", 0, userId));
            up.Add(dbIO.CreateParameter("NewPassword", "VARCHAR", 8000, newPassword.GetHash()));
            dbIO.ToDataTable("UPDATE BaseUser SET Password=@NewPassword WHERE Id=@UserId; SELECT 1 A", up);
            return (true, "Password changed");
        }

        public static (bool success, string message) RequestPasswordReset(string userName)
        {
            if (userName.IsNullOrEmpty()) return (false, "Username required");
            return (true, "If user exists, reset instructions sent");
        }

        public static (bool success, string message) ResetPasswordWithToken(string userName, string token, string newPassword)
        {
            if (token.IsNullOrEmpty()) return (false, "Token required");
            return (true, "Password reset");
        }
        #endregion

        #region Profile (self)
        public static JsonObject GetProfile(int userId) => BaseAdminUtils.GetUserProfile(userId);
        public static JsonObject UpdateProfile(int userId, Person update) => BaseAdminUtils.UpdateUserProfile(userId, update);
        #endregion
    }
}
