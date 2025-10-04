using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PowNet.Services;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class HttpContextHeaderTests
{
    [Fact]
    public void ErrorHeaders_Set_Status_Metadata()
    {
        var ctx = new DefaultHttpContext();
        var info = new ApiCallInfo("/p/q", "NSX", "CtlZ", "ActZ");
        ctx.AddInternalErrorHeaders(42, new Exception("err"), info);
        ctx.Response.Headers["X-Result-StatusCode"].ToString().Should().Be("500");
        ctx.Response.Headers["X-Execution-Duration"].ToString().Should().Be("42");
        ctx.AddUnauthorizedErrorHeaders(9, new UnauthorizedAccessException("u"), info);
        ctx.Response.Headers["X-Result-StatusCode"].ToString().Should().Be("401");
    }

    [Fact]
    public void NotFoundHeaders_Set_404()
    {
        var ctx = new DefaultHttpContext();
        var info = new ApiCallInfo("/nf", "NSY", "CtlY", "ActY");
        ctx.AddNotFoundErrorHeaders(7, info);
        ctx.Response.Headers["X-Result-StatusCode"].ToString().Should().Be("404");
    }
}
