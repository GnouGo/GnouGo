using System.Reflection;
using System.Text.Json.Nodes;
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

    [Fact]
    public void ConvertArguments_PreservesJsonArraysAndNestedObjects()
    {
        var arguments = new JsonObject
        {
            ["name"] = "slimfaas",
            ["schedules"] = new JsonArray(),
            ["metadata"] = new JsonObject
            {
                ["enabled"] = true,
                ["tags"] = new JsonArray("web", "summary")
            }
        };

        var result = InvokeConvertArguments(arguments);

        Assert.NotNull(result);
        Assert.Equal("slimfaas", result["name"]);

        var schedules = Assert.IsType<List<object?>>(result["schedules"]);
        Assert.Empty(schedules);

        var metadata = Assert.IsType<Dictionary<string, object?>>(result["metadata"]);
        Assert.Equal(true, metadata["enabled"]);

        var tags = Assert.IsType<List<object?>>(metadata["tags"]);
        Assert.Equal(["web", "summary"], tags);
    }

    [Fact]
    public void ConvertArguments_KeepsScalarValuesTyped()
    {
        var arguments = new JsonObject
        {
            ["text"] = "hello",
            ["flag"] = true,
            ["count"] = 3,
            ["ratio"] = 0.5
        };

        var result = InvokeConvertArguments(arguments);

        Assert.NotNull(result);
        Assert.IsType<string>(result["text"]);
        Assert.IsType<bool>(result["flag"]);
        Assert.True(result["count"] is int or long);
        Assert.IsType<double>(result["ratio"]);
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

    private static Dictionary<string, object?> InvokeConvertArguments(JsonNode arguments)
    {
        var adapterType = typeof(ConfiguredMcpClientFactory).Assembly.GetType("GnOuGo.Flow.Core.Runtime.McpSessionAdapter");
        Assert.NotNull(adapterType);

        var method = adapterType.GetMethod(
            "ConvertArguments",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var value = method.Invoke(null, [arguments]);
        return Assert.IsType<Dictionary<string, object?>>(value);
    }
}

