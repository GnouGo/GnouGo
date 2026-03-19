using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Flow.Cmd;
using Xunit;

namespace GnOuGo.Flow.Cmd.Tests;

public class CommandExecutionHostTests
{
    [Fact]
    public async Task RunAsync_ExecutesAllowlistedPowerShellCommand()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempDirectory();
        var policy = new CommandPolicy(new CmdServerSettings
        {
            AllowedShells = ["powershell"],
            AllowedWorkingRoots = [root],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["echo_test"] = new()
                {
                    Shell = "powershell",
                    Script = "Write-Output 'hello secure world'",
                    WorkingDirectory = root
                }
            }
        }, root);

        var host = new CommandExecutionHost(policy, NullLogger<CommandExecutionHost>.Instance);

        var result = await host.RunAsync("echo_test", null, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello secure world", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureWhenCommandExitCodeIsNonZero()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempDirectory();
        var policy = new CommandPolicy(new CmdServerSettings
        {
            AllowedShells = ["powershell"],
            AllowedWorkingRoots = [root],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["fail_test"] = new()
                {
                    Shell = "powershell",
                    Script = "Write-Error 'boom'; exit 3",
                    WorkingDirectory = root
                }
            }
        }, root);

        var host = new CommandExecutionHost(policy, NullLogger<CommandExecutionHost>.Instance);

        var result = await host.RunAsync("fail_test", null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("boom", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Flow.Cmd.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

