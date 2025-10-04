using FluentAssertions;
using Serilog.Events;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class LogManagerTests
{
    [Fact]
    public void Can_Set_Minimum_Level()
    {
        LogManager.SetMinimumLevel(LogEventLevel.Error);
        LogManager.LevelSwitch.MinimumLevel.Should().Be(LogEventLevel.Error);
    }
}
