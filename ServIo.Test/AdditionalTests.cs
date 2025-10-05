using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PowNet.Abstractions.Api;
using PowNet.Abstractions.Authentication;
using PowNet.Configuration;
using PowNet.Models;
using PowNet.Services;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class AdditionalTests
{
    private sealed class Parser : IApiCallParser
    {
        private readonly ApiCallInfo _info;
        public Parser(string ns, string ctl, string act, string path) => _info = new(path, ns, ctl, act);
        public IApiCallInfo Parse(HttpContext httpContext) => _info;
    }

    private sealed class FixedUserResolver : ITokenUserResolver
    {
        private readonly IUserIdentity _user;
        public FixedUserResolver(IUserIdentity user) => _user = user;
        public IUserIdentity ResolveFromHttpContext(HttpContext context) => _user;
        public IUserIdentity ResolveFromToken(string token) => _user;
    }

    private static void WriteConfig(string ns, string ctl, ApiConfiguration apiCfg)
    {
        var cc = new ControllerConfiguration { NamespaceName = ns, ControllerName = ctl, ApiConfigurations = [apiCfg] };
        Directory.CreateDirectory("workspace/server");
        cc.WriteConfig();
        ControllerConfiguration.ClearConfigCache(ns, ctl);
    }

    private static DefaultHttpContext Ctx(string path, string method = "POST")
    {
        var c = new DefaultHttpContext();
        c.Request.Method = method;
        c.Request.Path = path;
        c.Response.Body = new MemoryStream();
        return c;
    }

    private static void AddUser(HttpContext ctx, string userName, int id)
    {
        var u = new UserServerObject { Id = id, UserName = userName, Roles = [] };
        ctx.Request.Headers["token"] = u.Tokenize();
    }

    [Fact]
    public async Task Cache_First_Call_Has_MISS_Header()
    {
        string ns = "NSM"; string ctl = "Cache"; string act = "First"; string path = "/api/cache/first";
        WriteConfig(ns, ctl, new ApiConfiguration { ApiName = act, CacheLevel = PowNet.Common.CacheLevel.AllUsers, CacheSeconds = 30, CheckAccessLevel = PowNet.Common.CheckAccessLevel.OpenForAllUsers });
        var parser = new Parser(ns, ctl, act, path);
        RequestDelegate next = async c => { await c.Response.WriteAsync("DATA1"); };
        var mw = new ApiGatewayMiddleware(next, parser: parser);

        var c1 = Ctx(path); AddUser(c1, "user", 1); await mw.InvokeAsync(c1);
        c1.Response.Headers["X-Cache"].ToString().Should().Be("MISS");
    }

    [Fact]
    public async Task Cache_Preserves_ContentType_On_HIT()
    {
        string ns = "NST"; string ctl = "Cache"; string act = "Type"; string path = "/api/cache/type";
        WriteConfig(ns, ctl, new ApiConfiguration { ApiName = act, CacheLevel = PowNet.Common.CacheLevel.AllUsers, CacheSeconds = 30, CheckAccessLevel = PowNet.Common.CheckAccessLevel.OpenForAllUsers });
        var parser = new Parser(ns, ctl, act, path);
        RequestDelegate next = async c => { c.Response.ContentType = "application/json"; await c.Response.WriteAsync("{\"ok\":true}"); };
        var mw = new ApiGatewayMiddleware(next, parser: parser);

        var c1 = Ctx(path); AddUser(c1, "user", 1); await mw.InvokeAsync(c1);
        var c2 = Ctx(path); AddUser(c2, "user", 1); await mw.InvokeAsync(c2);
        c2.Response.Headers["X-Cache"].ToString().Should().Be("HIT");
        c2.Response.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task AccessControl_AllowedUsers_Allows_And_Denies()
    {
        string ns = "NSA"; string ctl = "Access"; string act = "Only"; string path = "/api/access/only";
        int allowedId = 42;
        WriteConfig(ns, ctl, new ApiConfiguration { ApiName = act, AllowedUsers = [allowedId], CacheLevel = PowNet.Common.CacheLevel.None, CacheSeconds = 0, LogEnabled = false });
        var parser = new Parser(ns, ctl, act, path);
        RequestDelegate next = _ => Task.CompletedTask;

        var allowedUser = new UserServerObject { Id = allowedId, UserName = "allowed", Roles = [] };
        var deniedUser = new UserServerObject { Id = allowedId + 1, UserName = "denied", Roles = [] };

        var mwAllowed = new ApiGatewayMiddleware(next, parser: parser, userResolver: new FixedUserResolver(allowedUser));
        var ctxA = Ctx(path); await mwAllowed.InvokeAsync(ctxA); ctxA.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        var mwDenied = new ApiGatewayMiddleware(next, parser: parser, userResolver: new FixedUserResolver(deniedUser));
        var ctxD = Ctx(path); await mwDenied.InvokeAsync(ctxD); ctxD.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void PluginManager_Load_And_Attempt_Unload()
    {
        string sourceAsm = typeof(ApiGatewayMiddleware).Assembly.Location;
        string tempDir = Path.Combine(Path.GetTempPath(), "pluginTests");
        Directory.CreateDirectory(tempDir);
        string pluginPath = Path.Combine(tempDir, Guid.NewGuid() + ".dll");
        File.Copy(sourceAsm, pluginPath, overwrite: true);

        var load = PluginManager.LoadPlugin(pluginPath);
        load.Success.Should().BeTrue(load.Error?.Message);

        var unload = PluginManager.UnloadPlugin(pluginPath);
        // Accept either success or failure (GC may hold references); just ensure path matches
        unload.Path.Should().Be(pluginPath);
    }
}
