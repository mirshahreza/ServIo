using System.Reflection;
using FluentAssertions;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class PluginManagerTests
{
    [Fact]
    public void Load_NotExisting_File_Fails()
    {
        var res = PluginManager.LoadPlugin(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".dll"));
        res.Success.Should().BeFalse();
        res.Error.Should().NotBeNull();
    }

    [Fact]
    public void Unload_NotLoaded_Returns_Failed()
    {
        var res = PluginManager.UnloadPlugin(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".dll"));
        res.Success.Should().BeFalse();
    }
}
