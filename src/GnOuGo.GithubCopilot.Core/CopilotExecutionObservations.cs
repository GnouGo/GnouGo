using GitHub.Copilot;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Captures provider execution receipts without interpreting tool names or assistant text.</summary>
internal sealed class CopilotExecutionObservations(string? sessionId = null, CopilotTransientSessionState? state = null)
{
    private long _sequence;
    private bool _sessionError;
    private bool _contextLimit;
    private bool _quiescent;
    private long _errorSequence;
    private long _idleSequence;
    private long _activitySequence;
    private readonly Dictionary<Guid, string> _eventHashes = [];
    private readonly List<SessionEvent> _observedEvents = [];
    private Guid? _firstEventId;
    private bool _conflictingEvents;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Dictionary<string, CopilotToolExecutionObservation> _calls = new(StringComparer.Ordinal);

    internal void Observe(SessionEvent evt)
    {
        lock (_gate)
        {
            _firstEventId ??= evt.Id;
            if (evt is not (SessionErrorEvent or SessionIdleEvent or ToolExecutionStartEvent or ToolExecutionCompleteEvent)) return;
            if (evt.Id != Guid.Empty)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EventSignature(evt))));
                if (_eventHashes.TryGetValue(evt.Id, out var previousHash)) { if (previousHash != hash) _conflictingEvents = true; return; }
                _eventHashes[evt.Id] = hash;
            }
            _observedEvents.Add(evt);
            var sequence = ++_sequence;
            if (evt is SessionErrorEvent error) { _sessionError = true; _contextLimit = error.Data.ErrorType == "context_limit"; _errorSequence = sequence; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            if (evt is SessionIdleEvent idle) { _quiescent = idle.Data.Aborted != true && idle.Data.Mode == SessionMode.Interactive; _idleSequence = sequence; _idle.TrySetResult(); }
            if (evt is ToolExecutionStartEvent start)
            {
                var data = start.Data;
                if (string.IsNullOrWhiteSpace(data.ToolCallId)) return;
                var previous = _calls.GetValueOrDefault(data.ToolCallId) ?? Empty(data.ToolCallId, data.ParentToolCallId);
                if (previous.StartedSequence is not null && previous.ToolName == data.ToolName && previous.ArgumentsJson == data.Arguments?.GetRawText() &&
                    previous.ParentToolCallId == data.ParentToolCallId && previous.IsShellCommand == (data.ShellToolInfo is not null)) return;
                _quiescent = false; _activitySequence = sequence; _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);

                _calls[data.ToolCallId] = previous with { ToolName = data.ToolName, ArgumentsJson = data.Arguments?.GetRawText(), ParentToolCallId = data.ParentToolCallId,
                    CopilotSessionId = sessionId, IsShellCommand = data.ShellToolInfo is not null,
                    ShellId = data.ShellToolInfo is not null && data.Arguments is { ValueKind: JsonValueKind.Object } args && args.TryGetProperty("shellId", out var shellId) && shellId.ValueKind == JsonValueKind.String ? shellId.GetString() : null,
                    StartedSequence = previous.StartedSequence ?? sequence,
                    ConflictingCompletion = previous.ConflictingCompletion || previous.StartedSequence is not null };
            }
            else if (evt is ToolExecutionCompleteEvent complete)
            {
                var data = complete.Data;
                if (string.IsNullOrWhiteSpace(data.ToolCallId)) return;
                var previous = _calls.GetValueOrDefault(data.ToolCallId) ?? Empty(data.ToolCallId, data.ParentToolCallId);
                var terminals = Terminals(data);
                foreach (var terminal in terminals)
                    if (terminal.OutputFilePath is { } path) state?.RegisterOutput(path);
                if (previous.CompletionObserved)
                {
                    if (previous.ToolSucceeded != data.Success || previous.ErrorCode != data.Error?.Code || !previous.Terminals.SequenceEqual(terminals))
                        _calls[data.ToolCallId] = previous with { ConflictingCompletion = true, ToolSucceeded = null };
                    return;
                }
                _activitySequence = sequence;
                _calls[data.ToolCallId] = previous with { CompletionObserved = true, ToolSucceeded = data.Success, Terminals = terminals, ErrorCode = data.Error?.Code, CompletedSequence = sequence, ShellId = previous.ShellId ?? terminals.Select(t => t.ShellId).FirstOrDefault(id => id is not null) };
            }
        }
    }

    private static CopilotTerminalObservation[] Terminals(ToolExecutionCompleteData data)
    {
        var terminals = (data.Result?.Contents ?? []).Select(content => content switch
                    {
                        ToolExecutionCompleteContentTerminal terminal => new CopilotTerminalObservation(terminal.Cwd, terminal.ExitCode, terminal.Text),
                        ToolExecutionCompleteContentShellExit shell => new CopilotTerminalObservation(shell.Cwd, shell.ExitCode, shell.OutputPreview)
                        { OutputTruncated = shell.OutputTruncated, OutputFilePath = shell.OutputFilePath, ShellId = shell.ShellId },
                        _ => null
                    }).OfType<CopilotTerminalObservation>().ToArray();
        if (data.ShellExecution?.ExitCode is { } exit && terminals.Length == 0)
            terminals = [new(null, exit, null)];
        return terminals;
    }

    private static string EventSignature(SessionEvent evt)
    {
        string?[] facts = evt switch
        {
            ToolExecutionStartEvent e => [e.Data.ToolCallId, e.Data.ParentToolCallId, e.Data.ToolName, e.Data.Arguments?.GetRawText(), e.Data.ShellToolInfo?.DisplayCommand, (e.Data.ShellToolInfo is not null).ToString()],
            ToolExecutionCompleteEvent e => [e.Data.ToolCallId, e.Data.ParentToolCallId, e.Data.Success.ToString(), e.Data.Error?.Code,
                JsonSerializer.Serialize(Terminals(e.Data), CopilotCoreJsonContext.Default.IReadOnlyListCopilotTerminalObservation)],
            SessionIdleEvent e => [e.Data.Aborted.ToString(), e.Data.Mode.ToString()],
            SessionErrorEvent e => [e.Data.ErrorType],
            _ => []
        };
        return evt.Type + JsonSerializer.Serialize(facts, CopilotCoreJsonContext.Default.IReadOnlyListString);
    }

    internal bool ContextLimit { get { lock (_gate) return _contextLimit; } }

    internal bool VerifiedTerminalCompletion
    {
        get
        {
            lock (_gate)
            {
                var all = _calls.Values.ToArray();
                return !_conflictingEvents && _quiescent && _idleSequence > Math.Max(_errorSequence, _activitySequence) &&
                    all.All(c => c.StartedSequence is not null && c.CompletionObserved && !c.ConflictingCompletion &&
                        (c.IsShellCommand == true ? VerifiedRefusal(c) || c.GetVerifiedTerminals(all) is not null
                            : c.Terminals.Count == 0 || c.GetVerifiedTerminals(all) is not null));
            }
        }
    }

    // The SDK's native shell denial is a pre-execution refusal, not an inferred process exit.
    private static bool VerifiedRefusal(CopilotToolExecutionObservation call) => call.ToolName is "bash" or "powershell" &&
        call.ToolSucceeded == false && call.ErrorCode == "denied" && call.Terminals.Count == 0;

    internal void MergeHistory(IReadOnlyList<SessionEvent> history)
    {
        lock (_gate)
        {
            var start = _firstEventId is null ? -1 : history.ToList().FindIndex(e => e.Id == _firstEventId);
            if (start < 0) return; // Never import previous invocations from the same session.
            var replay = history.Skip(start).ToList();
            var ids = replay.Select(e => e.Id).ToHashSet();
            var tail = _observedEvents.Where(e => e.Id == Guid.Empty || !ids.Contains(e.Id)).ToArray();
            // Conflicting copies remain observable; matching copies are deduplicated below.
            var observed = _observedEvents.ToArray();
            _calls.Clear(); _eventHashes.Clear(); _observedEvents.Clear();
            _sequence = _errorSequence = _idleSequence = _activitySequence = 0;
            _quiescent = _sessionError = _contextLimit = false;
            foreach (var evt in replay) Observe(evt);
            foreach (var evt in tail) Observe(evt);
            foreach (var evt in observed.Where(e => e.Id != Guid.Empty && ids.Contains(e.Id))) Observe(evt);
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
        lock (_gate)
        {
            var all = _calls.Values.ToArray();
            return all.Select(c => c.GetVerifiedTerminals(all) is { } terminals ? c with { Terminals = terminals } : c).ToArray();
        }
    }

    internal CopilotSendInterruptedException Interrupted(string handle, string sessionId, Exception error,
        CopilotExecutionBounds? bounds, CancellationToken ct, CopilotInferenceBudget? logical = null, IReadOnlyList<CopilotStreamEvent>? events = null)
    {
        lock (_gate)
            return new(new(handle, sessionId, "", null, events ?? [], Completed: false) { ToolExecutions = Snapshot(), OutputLogs = state?.OutputSnapshot() ?? new Dictionary<string, string>() },
                _sessionError && error is InvalidOperationException && bounds?.AdmissionStop is not null &&
                !bounds.TransportFailed && !ct.IsCancellationRequested,
                bounds is null && logical is { TransportFailed: false, AdmissionStop: null } && _contextLimit && VerifiedTerminalCompletion && !ct.IsCancellationRequested,
                bounds is null && logical is { TransportFailed: false } && VerifiedTerminalCompletion && !ct.IsCancellationRequested);
    }

    private static CopilotToolExecutionObservation Empty(string id, string? parent) => new(id, parent, null, null, false, null, false, [], null);
}
