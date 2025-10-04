using FluentAssertions;
using PowNet.Logging;
using Xunit;

namespace ServIo.Test;

public class LogManagerTests
{
    [Fact]
    public void Can_Get_PowNetLogger_Instance()
    {
        var logger = PowNetLogger.GetLogger("Test");
        logger.Should().NotBeNull();
    }
}
