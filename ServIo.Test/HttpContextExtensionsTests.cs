using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PowNet.Models;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class HttpContextExtensionsTests
{
    public static IEnumerable<object[]> PostFaceCases => new List<object[]>
    {
        new object[] { "POST", true },
        new object[] { "PUT", true },
        new object[] { "PATCH", true },
        new object[] { "GET", false }
    };

    [Fact]
    public void GetClientIp_Returns_Ip()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");
        ctx.GetClientIp().Should().Be("127.0.0.1");
    }

    [Theory]
    [MemberData(nameof(PostFaceCases))]
    public void IsPostFace_Works(string method, bool expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.IsPostFace().Should().Be(expected);
    }

    [Fact]
    public void AddCacheHeader_Adds_XCache()
    {
        var ctx = new DefaultHttpContext();
        ctx.AddCacheHeader("HIT");
        ctx.Response.Headers.ContainsKey("X-Cache").Should().BeTrue();
        ctx.Response.Headers["X-Cache"].ToString().Should().Be("HIT");
    }

    [Fact]
    public void ToUserServerObject_NoToken_Returns_Nobody()
    {
        var ctx = new DefaultHttpContext();
        var user = ctx.ToUserServerObject();
        user.UserName.Should().Be(UserServerObject.NobodyUserName);
    }

    [Fact]
    public void ToUserServerObject_WithToken_Returns_User()
    {
        var ctx = new DefaultHttpContext();
        var u = new UserServerObject { Id = 5, UserName = "bob", Roles = [] };
        var token = u.Tokenize();
        ctx.Request.Headers["token"] = token;
        var user = ctx.ToUserServerObject();
        user.UserName.Should().Be("bob");
    }

    [Fact]
    public void AddSuccessHeaders_Sets_Execution_Metadata()
    {
        var ctx = new DefaultHttpContext();
        var info = new PowNet.Services.ApiCallInfo("/x/y", "NS", "Ctl", "Act");
        ctx.AddSuccessHeaders(123, info);
        ctx.Response.Headers["X-Execution-Controller"].ToString().Should().Be("Ctl");
        ctx.Response.Headers["X-Execution-Action"].ToString().Should().Be("Act");
        ctx.Response.Headers["X-Execution-Duration"].ToString().Should().Be("123");
    }
}
