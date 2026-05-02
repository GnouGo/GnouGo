using System.Reflection;
using System.Text.Json.Nodes;

using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public class ConfiguredMcpClientFactoryTests
{
    [Fact]
    public async Task ServerMetadata_IncludesConfiguredDiscoveryTimeout()
    {
        await using var factory = new ConfiguredMcpClientFactory(new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["slow"] = new()
            {
                Type = "stdio",
                Description = "Slow cold-start server",
                DiscoveryTimeoutSeconds = 90,
                Command = "dotnet"
            }
        });

        var metadata = Assert.Single(factory.ServerMetadata);
        Assert.Equal("slow", metadata.Name);
        Assert.Equal("Slow cold-start server", metadata.Description);
        Assert.Equal(90, metadata.DiscoveryTimeoutSeconds);
    }

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
    public void FormatMcpFailureDiagnostics_IncludesLaunchExceptionChainAndStderrTail()
    {
        const string serverName = "diagnostic-browser";
        InvokeCreateStdioTransport(serverName, new McpServerOptions
        {
            Type = "stdio",
            Command = "tools/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp",
            Args = ["--sample"]
        });
        InvokeCaptureStdioErrorLine(serverName, "first stderr line");
        InvokeCaptureStdioErrorLine(serverName, "fatal browser crash");

        var ex = new InvalidOperationException(
            "The server shut down unexpectedly.",
            new IOException("The pipe is broken."));

        var diagnostics = InvokeFormatMcpFailureDiagnostics(serverName, ex);

        Assert.Contains("The server shut down unexpectedly.", diagnostics);
        Assert.Contains("System.InvalidOperationException", diagnostics);
        Assert.Contains("System.IO.IOException", diagnostics);
        Assert.Contains("configuredCommand=tools/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp", diagnostics);
        Assert.Contains("command=", diagnostics);
        Assert.Contains("args=--sample", diagnostics);
        Assert.Contains("workingDirectory=", diagnostics);
        Assert.Contains("first stderr line", diagnostics);
        Assert.Contains("fatal browser crash", diagnostics);
    }

    [Fact]
    public void ResolveStdioCommand_PreservesBarePathCommand()
    {
        var resolution = InvokeResolveStdioCommand("dotnet", AppContext.BaseDirectory);

        Assert.Equal("dotnet", resolution.Command);
        Assert.Null(resolution.WorkingDirectory);
    }

    [Fact]
    public void ResolveStdioCommand_ResolvesRelativeBundledToolExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-stdio-command-" + Guid.NewGuid().ToString("N"));
        var toolDirectory = Path.Combine(root, "tools", "GnOuGo.Browser.Mcp");
        Directory.CreateDirectory(toolDirectory);
        var executableName = OperatingSystem.IsWindows() ? "GnOuGo.Browser.Mcp.exe" : "GnOuGo.Browser.Mcp";
        var executable = Path.Combine(toolDirectory, executableName);
        File.WriteAllText(executable, string.Empty);

        try
        {
            var resolution = InvokeResolveStdioCommand("tools/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp", root);

            Assert.Equal(executable, resolution.Command);
            Assert.Equal(toolDirectory, resolution.WorkingDirectory);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
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

    [Fact]
    public void ResolveStdioWorkingDirectory_ReturnsExecutableDirectory_ForExistingCommandPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "gnougo-stdio-working-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, OperatingSystem.IsWindows() ? "tool.exe" : "tool");
        File.WriteAllText(executable, string.Empty);

        try
        {
            var result = InvokeResolveStdioWorkingDirectory(executable);

            Assert.Equal(root, result);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
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

    private static string? InvokeResolveStdioWorkingDirectory(string command)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "ResolveStdioWorkingDirectory",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        return (string?)method.Invoke(null, [command]);
    }

    private static (string Command, string? WorkingDirectory) InvokeResolveStdioCommand(string command, string baseDirectory)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(m => m.Name == "ResolveStdioCommand" && m.GetParameters().Length == 2);

        var value = method.Invoke(null, [command, baseDirectory]);
        Assert.NotNull(value);

        var type = value.GetType();
        var commandProperty = type.GetProperty("Command");
        var workingDirectoryProperty = type.GetProperty("WorkingDirectory");
        Assert.NotNull(commandProperty);
        Assert.NotNull(workingDirectoryProperty);

        return (
            Assert.IsType<string>(commandProperty.GetValue(value)),
            (string?)workingDirectoryProperty.GetValue(value));
    }

    private static void InvokeCreateStdioTransport(string serverName, McpServerOptions options)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "CreateStdioTransport",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(null, [serverName, options, null]);
    }

    private static void InvokeCaptureStdioErrorLine(string serverName, string line)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "CaptureStdioErrorLine",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(null, [serverName, line]);
    }

    private static string InvokeFormatMcpFailureDiagnostics(string serverName, Exception exception)
    {
        var method = typeof(ConfiguredMcpClientFactory).GetMethod(
            "FormatMcpFailureDiagnostics",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);
        var value = method.Invoke(null, [serverName, exception]);
        return Assert.IsType<string>(value);
    }
}

