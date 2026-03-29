using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Cmd.Mcp;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public class CommandExecutionHostTests
{
    [Fact]
    public async Task RunAsync_CanCreateSubdirectoryAndWriteMarkdownFileInDefaultWorkspace()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTempDirectory();
        var content = "# Today\r\n\r\n- Example note\r\n";
        var contentBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var policy = new CommandPolicy(new CmdServerSettings
        {
            DefaultWorkingDirectory = root,
            AllowedShells = ["powershell"],
            AllowedCommands = new Dictionary<string, AllowedCommandSettings>(StringComparer.OrdinalIgnoreCase)
            {
                ["write_note"] = new()
                {
                    Shell = "powershell",
                    Script = "$directory = {{directory}}; New-Item -ItemType Directory -Path $directory -Force | Out-Null; $filePath = Join-Path -Path $directory -ChildPath {{fileName}}; $content = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String({{contentBase64}})); [System.IO.File]::WriteAllText($filePath, $content, [System.Text.UTF8Encoding]::new($false)); Write-Output $filePath",
                    Parameters = new Dictionary<string, CommandParameterSettings>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["directory"] = new() { Required = true, Pattern = "^(?![\\\\/])(?!.*(?:^|[\\\\/])\\.\\.(?:[\\\\/]|$))[A-Za-z0-9_.\\\\/-]{1,120}$", MaxLength = 120 },
                        ["fileName"] = new() { Required = true, Pattern = "^[A-Za-z0-9_.-]{1,120}\\.md$", MaxLength = 120 },
                        ["contentBase64"] = new() { Required = true, Pattern = "^[A-Za-z0-9+/=\\r\\n]{1,4000}$", MaxLength = 4000 }
                    }
                }
            }
        }, root);

        var host = new CommandExecutionHost(policy, NullLogger<CommandExecutionHost>.Instance);
        var parametersJson = $$"""
                             {"directory":"notes","fileName":"today.md","contentBase64":"{{contentBase64}}"}
                             """;

        var result = await host.RunAsync(
            "write_note",
            parametersJson,
            null,
            CancellationToken.None);

        var expectedFilePath = Path.Combine(root, "notes", "today.md");

        Assert.True(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(Path.Combine(root, "notes")));
        Assert.True(File.Exists(expectedFilePath));
        Assert.Equal(content, File.ReadAllText(expectedFilePath));
        Assert.Contains("today.md", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

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
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Cmd.Mcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

