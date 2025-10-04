using FluentAssertions;
using PowNet.Configuration;
using Xunit;

namespace ServIo.Test;

public class ControllerConfigurationTests
{
    [Fact]
    public void Write_And_Read_Config_Works()
    {
        var cfg = new ControllerConfiguration
        {
            NamespaceName = "CfgNS",
            ControllerName = "CtlA",
            ApiConfigurations = [ new ApiConfiguration{ ApiName = "X", CacheSeconds = 5 } ]
        };
        Directory.CreateDirectory("workspace/server");
        cfg.WriteConfig();
        ControllerConfiguration.ClearConfigCache(cfg.NamespaceName, cfg.ControllerName);
        var loaded = ControllerConfiguration.GetConfig(cfg.NamespaceName, cfg.ControllerName);
        loaded.ControllerName.Should().Be("CtlA");
        loaded.ApiConfigurations.Should().ContainSingle(a => a.ApiName == "X");
    }

    [Fact]
    public void CacheKey_Generation_Is_Stable()
    {
        var k1 = ControllerConfiguration.GenerateCacheKey("A","B");
        var k2 = ControllerConfiguration.GenerateCacheKey("A","B");
        k1.Should().Be(k2);
    }
}
