using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Captures provider execution receipts without interpreting tool names or assistant text.</summary>
internal sealed class CopilotExecutionObservations
{
    private long _sequence;
    private bool _sessionError;
    private bool _contextLimit;
    private bool _quiescent;
    private long _errorSequence;
    private long _idleSequence;
    private long _activitySequence;
    private readonly HashSet<string> _shellCalls = new(StringComparer.Ordinal);
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Dictionary<string, CopilotToolExecutionObservation> _calls = new(StringComparer.Ordinal);

    internal void Observe(SessionEvent evt)
    {
        lock (_gate)
        {
            var sequence = ++_sequence;
            if (evt is SessionErrorEvent error) { _sessionError = true; _contextLimit = error.Data.ErrorType == "context_limit"; _errorSequence = sequence; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            if (evt is SessionIdleEvent idle) { _quiescent = idle.Data.Aborted != true && idle.Data.Mode == SessionMode.Interactive; _idleSequence = sequence; _idle.TrySetResult(); }
            if (evt is ToolExecutionStartEvent start)
            {
                var data = start.Data;
                if (string.IsNullOrWhiteSpace(data.ToolCallId)) return;
                var previous = _calls.GetValueOrDefault(data.ToolCallId) ?? Empty(data.ToolCallId, data.ParentToolCallId);
                if (previous.StartedSequence is not null && previous.ToolName == data.ToolName && previous.ArgumentsJson == data.Arguments?.GetRawText() &&
                    previous.ParentToolCallId == data.ParentToolCallId && _shellCalls.Contains(data.ToolCallId) == (data.ShellToolInfo is not null)) return;
                _quiescent = false; _activitySequence = sequence; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (data.ShellToolInfo is not null) _shellCalls.Add(data.ToolCallId);
                _calls[data.ToolCallId] = previous with { ToolName = data.ToolName, ArgumentsJson = data.Arguments?.GetRawText(), ParentToolCallId = data.ParentToolCallId, StartedSequence = previous.StartedSequence ?? sequence,
                    ConflictingCompletion = previous.ConflictingCompletion || previous.StartedSequence is not null };
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
                if (data.ShellExecution?.ExitCode is { } exit && terminals.Length == 0)
                    terminals = [new(null, exit, null)];
                if (previous.CompletionObserved)
                {
                    if (previous.ToolSucceeded != data.Success || previous.ErrorCode != data.Error?.Code || !previous.Terminals.SequenceEqual(terminals))
                        _calls[data.ToolCallId] = previous with { ConflictingCompletion = true, ToolSucceeded = null };
                    return;
                }
                _activitySequence = sequence;
                _calls[data.ToolCallId] = previous with { CompletionObserved = true, ToolSucceeded = data.Success, Terminals = terminals, ErrorCode = data.Error?.Code, CompletedSequence = sequence };
            }
        }
    }

    internal bool ContextLimit { get { lock (_gate) return _contextLimit; } }

    internal bool VerifiedTerminalCompletion
    {
        get
        {
            lock (_gate) return _quiescent && _idleSequence > Math.Max(_errorSequence, _activitySequence) &&
                _calls.Values.All(c => c.StartedSequence is not null && c.CompletionObserved && !c.ConflictingCompletion &&
                    c.Terminals.All(t => t.ExitCode is not null) &&
                    (!_shellCalls.Contains(c.ToolCallId) || c.Terminals.Any(t => t.ExitCode is not null)));
        }
    }

    internal async Task WaitForIdleAsync(CancellationToken ct)
    {
        Task idle;
        lock (_gate) { if (VerifiedTerminalCompletion) return; idle = _idle.Task; }
        try { await idle.WaitAsync(TimeSpan.FromSeconds(2), ct); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal IReadOnlyList<CopilotToolExecutionObservation> Snapshot()
    {
        lock (_gate) return _calls.Values.ToArray();
    }

    internal CopilotSendInterruptedException Interrupted(string handle, string sessionId, Exception error,
        CopilotExecutionBounds? bounds, CancellationToken ct, CopilotInferenceBudget? logical = null, IReadOnlyList<CopilotStreamEvent>? events = null)
    {
        lock (_gate)
            return new(new(handle, sessionId, "", null, events ?? [], Completed: false) { ToolExecutions = _calls.Values.ToArray() },
                _sessionError && error is InvalidOperationException && bounds?.AdmissionStop is not null &&
                !bounds.TransportFailed && !ct.IsCancellationRequested,
                bounds is null && logical is { TransportFailed: false, AdmissionStop: null } && _contextLimit && VerifiedTerminalCompletion && !ct.IsCancellationRequested,
                bounds is null && logical is { TransportFailed: false } && VerifiedTerminalCompletion && !ct.IsCancellationRequested);
    }

    private static CopilotToolExecutionObservation Empty(string id, string? parent) => new(id, parent, null, null, false, null, false, [], null);
}
