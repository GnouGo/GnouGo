using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Captures provider execution receipts without interpreting tool names or assistant text.</summary>
internal sealed class CopilotExecutionObservations
{
    private long _sequence;
    private readonly object _gate = new();
    private readonly Dictionary<string, CopilotToolExecutionObservation> _calls = new(StringComparer.Ordinal);

    internal void Observe(SessionEvent evt)
    {
        lock (_gate)
        {
            var sequence = ++_sequence;
            if (evt is ToolExecutionStartEvent start)
            {
                var data = start.Data;
                if (string.IsNullOrWhiteSpace(data.ToolCallId)) return;
                var previous = _calls.GetValueOrDefault(data.ToolCallId) ?? Empty(data.ToolCallId, data.ParentToolCallId);
                _calls[data.ToolCallId] = previous with { ToolName = data.ToolName, ArgumentsJson = data.Arguments?.GetRawText(), ParentToolCallId = data.ParentToolCallId, StartedSequence = previous.StartedSequence ?? sequence,
                    ConflictingCompletion = previous.ConflictingCompletion || previous.StartedSequence is not null && (previous.ToolName != data.ToolName || previous.ArgumentsJson != data.Arguments?.GetRawText()) };
            }
            else if (evt is ToolExecutionCompleteEvent complete)
            {
                var data = complete.Data;
                if (string.IsNullOrWhiteSpace(data.ToolCallId)) return;
                var previous = _calls.GetValueOrDefault(data.ToolCallId) ?? Empty(data.ToolCallId, data.ParentToolCallId);
                var terminals = (data.Result?.Contents ?? []).Select(content => content switch
                    {
                        ToolExecutionCompleteContentTerminal terminal => new CopilotTerminalObservation(terminal.Cwd, terminal.ExitCode, terminal.Text),
                        ToolExecutionCompleteContentShellExit shell => new CopilotTerminalObservation(shell.Cwd, shell.ExitCode, shell.OutputPreview)
                        { OutputTruncated = shell.OutputTruncated, OutputFilePath = shell.OutputFilePath, ShellId = shell.ShellId },
                        _ => null
                    }).OfType<CopilotTerminalObservation>().ToArray();
                if (previous.CompletionObserved)
                {
                    if (previous.ToolSucceeded != data.Success || previous.ErrorCode != data.Error?.Code || !previous.Terminals.SequenceEqual(terminals))
                        _calls[data.ToolCallId] = previous with { ConflictingCompletion = true, ToolSucceeded = null };
                    return;
                }
                _calls[data.ToolCallId] = previous with { CompletionObserved = true, ToolSucceeded = data.Success, Terminals = terminals, ErrorCode = data.Error?.Code, CompletedSequence = sequence };
            }
        }
    }

    internal IReadOnlyList<CopilotToolExecutionObservation> Snapshot()
    {
        lock (_gate) return _calls.Values.ToArray();
    }

    private static CopilotToolExecutionObservation Empty(string id, string? parent) => new(id, parent, null, null, false, null, false, [], null);
}
