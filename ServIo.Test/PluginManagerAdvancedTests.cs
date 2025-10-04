using System.Reflection;
using System.Runtime.Loader;
using FluentAssertions;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class PluginManagerAdvancedTests
{
    [Fact]
    public void LoadPlugin_Fails_For_Duplicate_Attempt_When_Invalid()
    {
        var tempDll = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".dll");
        File.WriteAllBytes(tempDll, new byte[]{0}); // invalid dll -> first load fails
        var first = PluginManager.LoadPlugin(tempDll);
        first.Success.Should().BeFalse();
        var second = PluginManager.LoadPlugin(tempDll);
        second.Success.Should().BeFalse();
        File.Delete(tempDll);
    }

    [Fact]
    public void UnloadPlugin_Failed_NotLoaded()
    {
        var res = PluginManager.UnloadPlugin(Path.Combine(Path.GetTempPath(), "not-exist.dll"));
        res.Success.Should().BeFalse();
    }
}
