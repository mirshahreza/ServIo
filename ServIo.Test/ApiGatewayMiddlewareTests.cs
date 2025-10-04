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

public class ApiGatewayMiddlewareTests
{
    private readonly RequestDelegate _nextOk = ctx => ctx.Response.WriteAsync("OK");

    private sealed class TestCallParser : IApiCallParser
    {
        private readonly ApiCallInfo _info;
        public TestCallParser(string ns, string controller, string action, string path)
            => _info = new ApiCallInfo(path, ns, controller, action);
        public IApiCallInfo Parse(HttpContext httpContext) => _info;
    }

    private static void WriteControllerConfig(string ns, string controller, ApiConfiguration config)
    {
        var cc = new ControllerConfiguration
        {
            NamespaceName = ns,
            ControllerName = controller,
            ApiConfigurations = [config]
        };
        Directory.CreateDirectory("workspace/server");
        cc.WriteConfig();
        ControllerConfiguration.ClearConfigCache(ns, controller);
    }

    private static DefaultHttpContext CreateContext(string path = "/v1/test/do", string method = "POST")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.TraceIdentifier = Guid.NewGuid().ToString();
        return ctx;
    }

    private static void AddUserToken(HttpContext ctx, string userName, int id = 10)
    {
        var user = new UserServerObject { Id = id, UserName = userName, Roles = [] };
        var token = user.Tokenize();
        ctx.Request.Headers["token"] = token;
    }

    [Fact]
    public async Task InvokeAsync_Skips_Non_PostLike()
    {
        var ctx = CreateContext(method: "GET");
        var called = false;
        RequestDelegate next = c => { called = true; return Task.CompletedTask; };
        var mw = new ApiGatewayMiddleware(next);
        await mw.InvokeAsync(ctx);
        called.Should().BeTrue();
    }

    [Fact]
    public async Task Returns_404_When_Controller_Or_Action_Missing()
    {
        var ctx = CreateContext();
        AddUserToken(ctx, "alpha");
        var parser = new TestCallParser("NS", "", "Do", ctx.Request.Path);
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new ApiGatewayMiddleware(next, parser: parser);
        await mw.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Caching_Works_Hit_On_Second_Call()
    {
        string ns = "NS1"; string controller = "Calc"; string action = "Sum"; string path = "/api/calc/sum";
        var apiConf = new ApiConfiguration
        {
            ApiName = action,
            CacheLevel = PowNet.Common.CacheLevel.AllUsers,
            CacheSeconds = 30,
            LogEnabled = false,
            CheckAccessLevel = PowNet.Common.CheckAccessLevel.OpenForAllUsers
        };
        WriteControllerConfig(ns, controller, apiConf);

        var parser = new TestCallParser(ns, controller, action, path);
        RequestDelegate next = async c => await c.Response.WriteAsync("DATA");
        var mw = new ApiGatewayMiddleware(next, parser: parser);

        var ctx1 = CreateContext(path); AddUserToken(ctx1, "user1"); ctx1.Response.Body = new MemoryStream(); await mw.InvokeAsync(ctx1);
        var ctx2 = CreateContext(path); AddUserToken(ctx2, "user1"); ctx2.Response.Body = new MemoryStream(); await mw.InvokeAsync(ctx2);
        ctx2.Response.Headers["X-Cache"].ToString().Should().Be("HIT");
    }

    [Fact]
    public async Task Unauthorized_Request_Yields_401()
    {
        string ns = "NS2"; string controller = "Calc"; string action = "Sub"; string path = "/api/calc/sub";
        var deniedUserId = 77;
        var apiConf = new ApiConfiguration { ApiName = action, CacheLevel = PowNet.Common.CacheLevel.None, CacheSeconds = 0, LogEnabled = false, DeniedUsers = [deniedUserId] };
        WriteControllerConfig(ns, controller, apiConf);
        var parser = new TestCallParser(ns, controller, action, path);
        RequestDelegate next = _ => Task.CompletedTask;
        var mw = new ApiGatewayMiddleware(next, parser: parser);
        var ctx = CreateContext(path); AddUserToken(ctx, "blocked", deniedUserId);
        await mw.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Exception_In_Next_Produces_500()
    {
        string ns = "NS3"; string controller = "Calc"; string action = "Mul"; string path = "/api/calc/mul";
        var apiConf = new ApiConfiguration { ApiName = action, CacheLevel = PowNet.Common.CacheLevel.None, CacheSeconds = 0, CheckAccessLevel = PowNet.Common.CheckAccessLevel.OpenForAllUsers };
        WriteControllerConfig(ns, controller, apiConf);
        var parser = new TestCallParser(ns, controller, action, path);
        RequestDelegate next = _ => throw new InvalidOperationException("boom");
        var mw = new ApiGatewayMiddleware(next, parser: parser);
        var ctx = CreateContext(path); AddUserToken(ctx, "userX");
        await mw.InvokeAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    private sealed class RecordingActivityLogger : IActivityLogger
    {
        public int Count;
        public void LogActivity(HttpContext context, IUserIdentity user, IApiCallInfo call, string rowId, bool success, string message, int durationMs) => Count++;
        public void LogError(string message, Exception? ex = null) { }
        public void LogDebug(string message) { }
    }

    [Fact]
    public async Task ActivityLogger_Invoked_On_Success_And_Error()
    {
        string ns = "NS4"; string controller = "Calc"; string action = "Div"; string path = "/api/calc/div";
        var apiConf = new ApiConfiguration { ApiName = action, CacheLevel = PowNet.Common.CacheLevel.None, CacheSeconds = 0, LogEnabled = true, CheckAccessLevel = PowNet.Common.CheckAccessLevel.OpenForAllUsers };
        WriteControllerConfig(ns, controller, apiConf);
        var parser = new TestCallParser(ns, controller, action, path);
        var recorder = new RecordingActivityLogger();
        int throws = 0;
        RequestDelegate next = _ =>
        {
            if (throws++ == 0) throw new Exception("fail once");
            return Task.CompletedTask;
        };
        var mw = new ApiGatewayMiddleware(next, parser: parser, activity: recorder);
        var ctx1 = CreateContext(path); AddUserToken(ctx1, "user1"); await mw.InvokeAsync(ctx1); // error
        var ctx2 = CreateContext(path); AddUserToken(ctx2, "user1"); await mw.InvokeAsync(ctx2); // success
        recorder.Count.Should().Be(2);
    }
}
