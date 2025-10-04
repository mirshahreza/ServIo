using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PowNet.Abstractions.Api;
using PowNet.Configuration;
using PowNet.Models;
using PowNet.Services;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class ApiGatewayCachingPerUserTests
{
    private sealed class TestParser : IApiCallParser
    {
        private readonly ApiCallInfo _info;
        public TestParser(string ns, string ctl, string act, string path) => _info = new(path, ns, ctl, act);
        public IApiCallInfo Parse(HttpContext httpContext) => _info;
    }

    private static DefaultHttpContext Ctx(string path)
    {
        var c = new DefaultHttpContext();
        c.Request.Method = "POST";
        c.Request.Path = path;
        c.Response.Body = new MemoryStream();
        return c;
    }

    private static void AddUser(HttpContext ctx, string userName, int id)
    {
        var u = new UserServerObject { Id = id, UserName = userName, Roles = [] };
        ctx.Request.Headers["token"] = u.Tokenize();
    }

    private static void WriteConfig(string ns, string ctl, ApiConfiguration apiCfg)
    {
        var cc = new ControllerConfiguration{ NamespaceName = ns, ControllerName = ctl, ApiConfigurations = [apiCfg] };
        Directory.CreateDirectory("workspace/server");
        cc.WriteConfig();
        ControllerConfiguration.ClearConfigCache(ns, ctl);
    }

    [Fact]
    public async Task PerUser_Cache_Is_Isolated()
    {
        string ns = "NSP"; string ctl = "PerUser"; string act = "Data"; string path = "/api/per/data";
        WriteConfig(ns, ctl, new ApiConfiguration{ ApiName = act, CacheLevel = PowNet.Common.CacheLevel.PerUser, CacheSeconds = 30, CheckAccessLevel = PowNet.Common.CheckAccessLevel.OpenForAllUsers });
        var parser = new TestParser(ns, ctl, act, path);
        int executions = 0;
        RequestDelegate next = async c => { executions++; await c.Response.WriteAsync("VALUE" + executions); };
        var mw = new ApiGatewayMiddleware(next, parser: parser);

        var ctxA1 = Ctx(path); AddUser(ctxA1, "alice", 1); await mw.InvokeAsync(ctxA1); ctxA1.Response.Body.Position = 0; new StreamReader(ctxA1.Response.Body).ReadToEnd().Should().Contain("VALUE1");
        var ctxA2 = Ctx(path); AddUser(ctxA2, "alice", 1); await mw.InvokeAsync(ctxA2); ctxA2.Response.Headers["X-Cache"].ToString().Should().Be("HIT");

        var ctxB1 = Ctx(path); AddUser(ctxB1, "bob", 2); await mw.InvokeAsync(ctxB1); ctxB1.Response.Body.Position = 0; var b1 = new StreamReader(ctxB1.Response.Body).ReadToEnd(); b1.Should().Contain("VALUE2");
        var ctxB2 = Ctx(path); AddUser(ctxB2, "bob", 2); await mw.InvokeAsync(ctxB2); ctxB2.Response.Headers["X-Cache"].ToString().Should().Be("HIT");

        executions.Should().Be(2); // A1, B1 only; second calls are HITs
    }

    [Fact]
    public async Task Cache_Expires_After_Ttl()
    {
        string ns = "NSE"; string ctl = "Expire"; string act = "Get"; string path = "/api/exp/get";
        WriteConfig(ns, ctl, new ApiConfiguration{ ApiName = act, CacheLevel = PowNet.Common.CacheLevel.AllUsers, CacheSeconds = 1, CheckAccessLevel = PowNet.Common.CheckAccessLevel.OpenForAllUsers });
        var parser = new TestParser(ns, ctl, act, path);
        int executions = 0;
        RequestDelegate next = async c => { executions++; await c.Response.WriteAsync("VAL" + executions); };
        var mw = new ApiGatewayMiddleware(next, parser: parser);

        var c1 = Ctx(path); AddUser(c1, "user", 10); await mw.InvokeAsync(c1); c1.Response.Body.Position = 0; new StreamReader(c1.Response.Body).ReadToEnd().Should().Contain("VAL1");
        await Task.Delay(1100);
        var c2 = Ctx(path); AddUser(c2, "user", 10); await mw.InvokeAsync(c2);
        executions.Should().BeGreaterThanOrEqualTo(1);
    }
}
