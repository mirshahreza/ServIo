using System.Reflection;
using FluentAssertions;
using ServIo;
using Xunit;

namespace ServIo.Test;

public class PluginManagerTests
{
    [Fact]
    public void Normalize_Load_Unload_NotExisting_File_Fails()
    {
        var res = PluginManager.LoadPlugin("c:/not-exist/abc.dll");
        res.Success.Should().BeFalse();
        res.Error.Should().NotBeNull();
    }
}
