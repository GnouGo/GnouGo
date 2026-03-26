using System.Text.Json.Nodes;
using GnOuGo.Cmd.Mcp;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public class CommandPolicyTests
{
    [Fact]
    public void ResolveWorkingDirectory_AllowsChildDirectoryInsideAllowedRoot()
    {
        var root = CreateTempDirectory();
        var child = Directory.CreateDirectory(Path.Combine(root, "child"));
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'");

        var resolved = policy.ResolveWorkingDirectory(child.FullName);

        Assert.Equal(child.FullName, resolved);
    }

    [Fact]
    public void ResolveWorkingDirectory_RejectsPathOutsideAllowedRoots()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'");

        var ex = Assert.Throws<InvalidOperationException>(() => policy.ResolveWorkingDirectory(outside));

        Assert.Contains("outside the allowed roots", ex.Message);
    }

    [Fact]
    public void RenderScript_EscapesPowerShellParameters()
    {
        var root = CreateTempDirectory();
        var settings = CreateSettings(root, "Write-Output {{value}}", parameters: new Dictionary<string, CommandParameterSettings>
        {
            ["value"] = new() { Required = true, Pattern = "^[A-Za-z' ]+$", MaxLength = 50 }
        });
        var policy = new CommandPolicy(settings, root);
        var command = policy.GetRequiredCommand("test");

        var rendered = policy.RenderScript(command, new JsonObject { ["value"] = "O'Brien" });

        Assert.Equal("Write-Output 'O''Brien'", rendered);
    }

    [Fact]
    public void RenderScript_RejectsUnknownParameter()
    {
        var root = CreateTempDirectory();
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'");
        var command = policy.GetRequiredCommand("test");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.RenderScript(command, new JsonObject { ["extra"] = "value" }));

        Assert.Contains("not declared", ex.Message);
    }

    [Fact]
    public void ResolveTimeoutMs_ClampsRequestedTimeoutToPolicyMaximum()
    {
        var root = CreateTempDirectory();
        var policy = CreatePolicy(root, commandScript: "Write-Output 'ok'", maxTimeoutMs: 5000);
        var command = policy.GetRequiredCommand("test");

        var timeout = policy.ResolveTimeoutMs(command, requestedTimeoutMs: 99999);

        Assert.Equal(5000, timeout);
    }

    private static CommandPolicy CreatePolicy(string root, string commandScript, int maxTimeoutMs = 30000)
        => new(CreateSettings(root, commandScript, maxTimeoutMs: maxTimeoutMs), root);

    private static CmdServerSettings CreateSettings(
        string root,
        string commandScript,
        Dictionary<string, CommandParameterSettings>? parameters = null,
        int maxTimeoutMs = 30000)
        => new()
        {
            MaxTimeoutMs = maxTimeoutMs,
            AllowedShells = ["powershell", "sh"],
            AllowedWorkingRoots = [root],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = new()
                {
                    Shell = "powershell",
                    Script = commandScript,
                    WorkingDirectory = root,
                    Parameters = parameters ?? new Dictionary<string, CommandParameterSettings>(StringComparer.OrdinalIgnoreCase)
                }
            }
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Cmd.Mcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

