using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public sealed class WindowsCommandExecutionTests
{
    [Fact]
    public async Task DefaultLaunchUsesOnlyTheSelectedShellsBuiltInModulePath()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires native Windows PowerShell.");
        var root = Directory.CreateTempSubdirectory("gnougo-modules-").FullName;
        try
        {
            var policy = new CommandPolicy(new CmdServerSettings { DefaultWorkingDirectory = root }, root);
            Assert.DoesNotContain("PSModulePath", policy.BuildEnvironment().Keys, StringComparer.OrdinalIgnoreCase);
            var expected = Path.Combine(Path.GetDirectoryName(policy.ResolveShell("powershell").ExecutablePath)!, "Modules");
            var host = Host(root, new() { Shell = "powershell", Script = "[Console]::WriteLine($env:PSModulePath)" });
            var result = await host.RunAsync("probe", null, null, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.ErrorMessage); Assert.Equal(expected, result.Stdout!.TrimEnd('\r', '\n'), ignoreCase: true);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task PrivateEofPreservesRepeatedCommandsUnicodeQuotesAndExitCodes(int exitCode)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires native Windows PowerShell.");
        var root = Directory.CreateTempSubdirectory("gnougo-stdin-").FullName;
        try
        {
            var host = Host(root, new() { Shell = "powershell", Script =
                "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); if ($null -ne [Console]::ReadLine()) { exit 99 }; [Console]::WriteLine({{text}}); [Console]::Error.WriteLine('observed stderr'); exit " + exitCode,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["text"] = new() { Required = true, MaxLength = 100 } } });
            const string expected = "café 'quoted' \"double\" 日本語";
            for (var i = 0; i < 3; i++)
            {
                var result = await host.RunAsync("probe", new JsonObject { ["text"] = expected }.ToJsonString(), null, TestContext.Current.CancellationToken);
                Assert.False(result.TimedOut); Assert.Equal(exitCode, result.ExitCode); Assert.Equal(exitCode == 0, result.Success);
                Assert.Equal(expected, result.Stdout!.TrimEnd('\r', '\n'));
                Assert.Equal("observed stderr", result.Stderr!.TrimEnd('\r', '\n'));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationTerminateTheChildTree(bool cancel)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires native Windows PowerShell.");
        var root = Directory.CreateTempSubdirectory("gnougo-process-tree-").FullName;
        Process? child = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var host = Host(root, new() { Shell = "powershell", Script =
                "$start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path, '-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 60'); $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $child = [Diagnostics.Process]::Start($start); [IO.File]::WriteAllText('child.pid', [string]$child.Id); Start-Sleep -Seconds 60" });
            var running = host.RunAsync("probe", null, 5000, cancellation.Token);
            var marker = Path.Combine(root, "child.pid"); var timer = Stopwatch.StartNew();
            while (!File.Exists(marker) && !running.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(4))
                await Task.Delay(25, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(marker), "The child must actually start before testing tree termination.");
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(child.HasExited);
            if (cancel) cancellation.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            Assert.False(result.Success); Assert.True(result.TimedOut);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(child.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (child is not null) { if (!child.HasExited) child.Kill(entireProcessTree: true); child.Dispose(); }
            Directory.Delete(root, recursive: true);
        }
    }

    private static CommandExecutionHost Host(string root, AllowedCommandSettings command) => new(new CommandPolicy(new CmdServerSettings
    {
        DefaultWorkingDirectory = root, AllowedShells = ["powershell"],
        AllowedCommands = new(StringComparer.OrdinalIgnoreCase) { ["probe"] = command }
    }, root), NullLogger<CommandExecutionHost>.Instance);
}
