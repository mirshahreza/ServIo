using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
    public void AddCacheHeaders_Adds_XCache()
    {
        var ctx = new DefaultHttpContext();
        ctx.AddCacheHeaders();
        ctx.Response.Headers.ContainsKey("X-Cache").Should().BeTrue();
    }
}
