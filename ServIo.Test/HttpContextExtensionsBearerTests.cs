using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PowNet.Models;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class HttpContextExtensionsBearerTests
{
    [Fact]
    public void TokenToUserServerObjectNullable_Null_Returns_Nobody()
    {
        var user = HttpContextExtensions.TokenToUserServerObjectNullable(null);
        user.Should().NotBeNull();
        user.UserName.Should().Be(UserServerObject.NobodyUserName);
    }

    [Fact]
    public void TokenToUserServerObjectNullable_Bearer_Prefix_Parsed()
    {
        var original = new UserServerObject { Id = 99, UserName = "bearerUser", Roles = [] };
        string token = original.Tokenize();
        string bearer = "Bearer " + token;
        var parsed = HttpContextExtensions.TokenToUserServerObjectNullable(bearer);
        parsed.UserName.Should().Be("bearerUser");
        parsed.Id.Should().Be(99);
    }

    [Fact]
    public void ToUserServerObject_From_HttpContext_With_Bearer()
    {
        var ctx = new DefaultHttpContext();
        var u = new UserServerObject { Id = 7, UserName = "ctxUser", Roles = [] };
        ctx.Request.Headers["token"] = "Bearer " + u.Tokenize();
        var parsed = ctx.ToUserServerObject();
        parsed.UserName.Should().Be("ctxUser");
        parsed.Id.Should().Be(7);
    }
}
