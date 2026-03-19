using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.Flow.Cmd;

public sealed class CommandExecutionHost
{
    private readonly CommandPolicy _policy;
    private readonly ILogger<CommandExecutionHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CommandExecutionHost(
        CommandPolicy policy,
        ILogger<CommandExecutionHost> logger)
    {
        _policy = policy;
        _logger = logger;
    }

    public IReadOnlyList<CmdAllowedCommandInfo> ListAllowedCommands() => _policy.ListAllowedCommands();

    public CmdPolicyInfo GetPolicy() => _policy.DescribePolicy();

    public async Task<CmdRunResult> RunAsync(
        string commandName,
        string? parametersJson,
        int? timeoutMs,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var command = _policy.GetRequiredCommand(commandName);
            var shell = _policy.ResolveShell(command.Shell);
            var parameters = ParseParameters(parametersJson);
            var script = _policy.RenderScript(command, parameters);
            var workingDirectory = _policy.ResolveWorkingDirectory(command.WorkingDirectory);
            var effectiveTimeoutMs = _policy.ResolveTimeoutMs(command, timeoutMs);
            var outputLimit = _policy.ResolveOutputLimit(command);
            var environment = _policy.BuildEnvironment();

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = shell.ExecutablePath,
                    Arguments = shell.BuildArguments(script),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.Environment.Clear();
            foreach (var variable in environment)
                process.StartInfo.Environment[variable.Key] = variable.Value;

            var startedAt = DateTimeOffset.UtcNow;
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start command '{commandName}'.");

            _logger.LogInformation(
                "Executing allowed command {CommandName} with shell {Shell} in {WorkingDirectory}",
                commandName,
                shell.LogicalName,
                workingDirectory);

            var stdoutTask = ReadCappedAsync(process.StandardOutput, outputLimit);
            var stderrTask = ReadCappedAsync(process.StandardError, outputLimit);
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var delayTask = Task.Delay(effectiveTimeoutMs, cancellationToken);

            var completed = await Task.WhenAny(exitTask, delayTask);
            var timedOut = completed == delayTask;

            if (timedOut)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited.
                }
            }
            else
            {
                await exitTask;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var finishedAt = DateTimeOffset.UtcNow;
            var exitCode = timedOut ? -1 : process.ExitCode;
            var success = !timedOut && exitCode == 0;

            return new CmdRunResult(
                CommandName: commandName,
                Shell: shell.LogicalName,
                WorkingDirectory: workingDirectory,
                ExitCode: exitCode,
                Success: success,
                TimedOut: timedOut,
                Stdout: stdout.Text,
                Stderr: stderr.Text,
                OutputTruncated: stdout.Truncated || stderr.Truncated,
                StartedAtUtc: startedAt,
                FinishedAtUtc: finishedAt,
                DurationMs: (finishedAt - startedAt).TotalMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static JsonObject? ParseParameters(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(parametersJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("parametersJson must be a valid JSON object string.", ex);
        }

        if (node is not JsonObject obj)
            throw new InvalidOperationException("parametersJson must deserialize to a JSON object.");

        foreach (var kv in obj)
        {
            if (kv.Value is null)
                continue;

            if (kv.Value is not JsonValue value || !value.TryGetValue<string>(out _))
                throw new InvalidOperationException("Command parameters must be simple JSON string values.");
        }

        return obj;
    }

    private static async Task<CappedTextResult> ReadCappedAsync(StreamReader reader, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[1024];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read == 0)
                break;

            if (builder.Length < maxCharacters)
            {
                var toCopy = Math.Min(maxCharacters - builder.Length, read);
                builder.Append(buffer, 0, toCopy);
                if (toCopy < read)
                    truncated = true;
            }
            else
            {
                truncated = true;
            }
        }

        return new CappedTextResult(builder.ToString(), truncated);
    }

    private sealed record CappedTextResult(string Text, bool Truncated);
}

public sealed record CmdRunResult(
    string CommandName,
    string Shell,
    string WorkingDirectory,
    int ExitCode,
    bool Success,
    bool TimedOut,
    string Stdout,
    string Stderr,
    bool OutputTruncated,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    double DurationMs);

