using FluentAssertions;
using PowNet.Logging;
using Xunit;

namespace ServIo.Test;

public class LogManagerAdvancedTests
{
    [Fact]
    public void Can_Set_Global_Log_Level()
    {
        PowNetLogger.SetGlobalLogLevel(LogLevel.Debug);
        var logger = PowNetLogger.GetLogger("AdvTest");
        logger.Should().NotBeNull();
        // No direct getter exposed; just ensure no exception
    }
}
