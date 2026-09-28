using System.Diagnostics;
using System.Text;
using GnOuGo.Cmd.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public sealed class PowerShellLaunchDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task IsolatedPowerShellLaunchCompletesWithClosedInput()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires native Windows PowerShell.");
        var root = Path.Combine(Path.GetTempPath(), "gnougo-shell-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string script = "Write-Output 'isolated probe'; exit 0";
            var policy = new CommandPolicy(new CmdServerSettings { DefaultWorkingDirectory = root,
                AllowedCommands = new(StringComparer.OrdinalIgnoreCase) { ["probe"] = new() { Shell = "powershell", Script = script } } }, root);
            var shell = policy.ResolveShell("powershell");
            var environment = policy.BuildEnvironment();
            output.WriteLine("Shell={0}; version={1}; environment keys={2}", Path.GetFileName(shell.ExecutablePath),
                FileVersionInfo.GetVersionInfo(shell.ExecutablePath).FileVersion ?? "unknown", string.Join(',', environment.Keys.Order()));
            var baseline = await new CommandExecutionHost(policy, NullLogger<CommandExecutionHost>.Instance)
                .RunAsync("probe", null, null, TestContext.Current.CancellationToken);
            output.WriteLine("Host: success={0}; exit={1}; timeout={2}; elapsed={3}; stdout={4}; stderr={5}",
                baseline.Success, baseline.ExitCode, baseline.TimedOut, baseline.DurationMs, baseline.Stdout ?? "", baseline.Stderr ?? "");
            await Probe("current", shell.ExecutablePath, shell.BuildArguments(script), true);
            await Probe("text", shell.ExecutablePath, "-InputFormat Text -OutputFormat Text " + shell.BuildArguments(script), true);
            await Probe("encoded", shell.ExecutablePath, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -InputFormat Text -OutputFormat Text -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), true);
            await Probe("console", shell.ExecutablePath, shell.BuildArguments(script), false);
            var alternate = Environment.GetEnvironmentVariable("ProgramFiles") is { } files ? Path.Combine(files, "PowerShell", "7", "pwsh.exe") : "";
            if (File.Exists(alternate)) await Probe("pwsh", alternate, shell.BuildArguments(script), true);
            Assert.True(baseline.Success, "The actual Cmd PowerShell launch must complete under its existing timeout.");

            async Task Probe(string name, string executable, string arguments, bool noWindow)
            {
                using var process = new Process { StartInfo = new() { FileName = executable, Arguments = arguments,
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = noWindow,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                process.StartInfo.Environment.Clear();
                foreach (var pair in environment) process.StartInfo.Environment[pair.Key] = pair.Value;
                var timer = Stopwatch.StartNew(); process.Start(); var started = timer.ElapsedMilliseconds;
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                var exited = process.WaitForExitAsync(TestContext.Current.CancellationToken);
                var timeout = await Task.WhenAny(exited, Task.Delay(10_000, TestContext.Current.CancellationToken)) != exited;
                if (timeout) process.Kill(entireProcessTree: true);
                await exited.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
                var text = await stdout; var error = await stderr;
                output.WriteLine("Probe={0}; startMs={1}; totalMs={2}; exit={3}; timeout={4}; stdout={5}; stderr={6}",
                    name, started, timer.ElapsedMilliseconds, process.ExitCode, timeout, text[..Math.Min(256, text.Length)], error[..Math.Min(512, error.Length)]);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
