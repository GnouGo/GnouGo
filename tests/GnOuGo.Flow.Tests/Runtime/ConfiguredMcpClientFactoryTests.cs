using System.Reflection;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class ConfiguredMcpClientFactoryTests
{
    [Fact]
    public void IsUnexpectedServerExit_ReturnsTrue_ForNestedProcessExitMessage()
    {
        var ex = new InvalidOperationException(
            "outer",
            new Exception("MCP server process exited unexpectedly Server's stderr tail: ..."));

        Assert.True(InvokeIsUnexpectedServerExit(ex));
    }

    [Theory]
    [InlineData("The pipe is broken.")]
    [InlineData("The connection is closed.")]
    [InlineData("Cannot access a disposed object.")]
    public void IsUnexpectedServerExit_ReturnsTrue_ForKnownDisconnectedTransportMessages(string message)
    {
        Assert.True(InvokeIsUnexpectedServerExit(new Exception(message)));
    }

    [Fact]
    public void IsUnexpectedServerExit_ReturnsFalse_ForUnrelatedErrors()
    {
        Assert.False(InvokeIsUnexpectedServerExit(new Exception("validation failed")));
    }

    private static bool InvokeIsUnexpectedServerExit(Exception ex)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "IsUnexpectedServerExit",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        var value = method.Invoke(null, new object[] { ex });
        Assert.NotNull(value);
        return (bool)value;
    }
}

