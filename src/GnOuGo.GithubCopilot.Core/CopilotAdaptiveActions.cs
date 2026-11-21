using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Invocation-local action admissions and observations for an explicitly capped task.</summary>
public sealed class CopilotAdaptiveActions(Func<string, JsonObject, Task> persist, IReadOnlySet<string> verificationCommands)
{
    public IReadOnlySet<string> ObservationTools { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    private sealed record Entry(string Session, string Id, string Tool, string Signature, bool Observation, bool Verification, int Generation);
    internal Func<IReadOnlyList<CopilotToolExecutionObservation>>? ExecutionObservations { get; set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _claimedHostCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _effects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Generation, string Result, int Repeats)> _observations = new(StringComparer.Ordinal);
    private int _generation;
    private volatile bool _unresolved;
    internal bool Unresolved => _unresolved;

    internal async Task<string?> AdmitAsync(CopilotExecutionBounds bounds, string? session, string? id, string tool, object? arguments)
    {
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(id))
            return "The SDK did not identify the tool invocation.";
        await _gate.WaitAsync();
        try
        {
            var key = Identity(session, id);
            var args = CopilotProjectFileTool.Arguments(arguments);
            var shell = tool is "bash" or "powershell";
            var command = shell && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("command", out var text) &&
                text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            var signature = Identity(tool, command is null ? Fingerprint(args) : Hash(command));
            var observation = ObservationTools.Contains(tool) || tool is "project_read" or "project_list" or "project_stat" or "read_bash" or "read_powershell";
            if (_unresolved) return "Inspect the original action receipt before dispatching more work.";
            if (_identities.Contains(key) || _pending.Values.Any(a => a.Signature == signature))
                return "An identical operation was already admitted. Reconcile its observed outcome before repeating it.";
            if (!observation && _effects.Count > 0)
            {
                var receipts = ExecutionObservations?.Invoke() ?? [];
                foreach (var effect in _effects.Values)
                {
                    var matches = receipts.Where(r => r.CopilotSessionId == effect.Session && r.ToolCallId == effect.Id &&
                        r.ToolName == effect.Tool && r.StartedSequence.HasValue && r.CompletionObserved && !r.ConflictingCompletion).ToArray();
                    if (matches.Length != 1 || (effect.Tool is "bash" or "powershell" ?
                        matches[0].GetVerifiedTerminals(receipts) is not { Count: > 0 } : matches[0].ToolSucceeded != true))
                        return "An earlier effect has no verified terminal receipt. Observe it before another effect.";
                }
                if (_effects.TryGetValue(signature, out var previousEffect) &&
                    (!previousEffect.Verification || _generation == previousEffect.Generation))
                    return "Reuse the verified effect. Repeat a verification command only after a workspace change.";
            }
            if (observation && _observations.TryGetValue(signature, out var prior) && prior.Generation == _generation && prior.Repeats >= 2)
                return "Two reads returned identical evidence. Reuse it or select a different observation.";
            if (!bounds.TryAdmitSdkTool(tool)) return "The approved action scope, limit or deadline was reached.";
            _identities.Add(key);
            var entry = new Entry(session, id, tool, signature, observation, command is not null && verificationCommands.Contains(command), _generation);
            _pending[key] = entry;
            if (!observation) _effects[signature] = entry;
            await persist(key, Receipt(entry, "admitted", null));
            if (bounds.Stopped || DateTimeOffset.UtcNow >= bounds.Deadline)
            {
                _unresolved = true;
                return "The action admission expired before dispatch.";
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _unresolved = true;
            return "The host could not retain the action intent.";
        }
        finally { _gate.Release(); }
    }

    internal async Task ClaimHostToolAsync(string tool, JsonElement arguments, CancellationToken ct)
    {
        var signature = Identity(tool, Fingerprint(arguments));
        await _gate.WaitAsync(ct);
        try
        {
            var admission = _pending.SingleOrDefault(pair => pair.Value.Tool == tool && pair.Value.Signature == signature);
            if (_unresolved || admission.Value is null || !_claimedHostCalls.Add(admission.Key))
                throw new InvalidOperationException("Host dispatch requires an unused journaled SDK admission for these exact arguments.");
        }
        finally { _gate.Release(); }
    }

    internal async Task CompleteAsync(CopilotExecutionBounds bounds, string? session, string? id, string tool, object? result, bool success)
    {
        await _gate.WaitAsync();
        try
        {
            var key = Identity(session, id);
            if (!_pending.Remove(key, out var entry) || entry.Tool != tool)
            {
                _unresolved = true;
                return;
            }
            var observed = Fingerprint(result);
            await persist(key, Receipt(entry, success ? "observed" : "failed", observed));
            if (entry.Observation)
            {
                var repeats = _observations.TryGetValue(entry.Signature, out var previous) &&
                    previous.Generation == entry.Generation && previous.Result == observed ? previous.Repeats + 1 : 1;
                _observations[entry.Signature] = (entry.Generation, observed, repeats);
            }
            else if (success && !entry.Verification) _generation++;
            // A failed shell may still have an authoritative nonzero terminal exit.
            // The next effect must reconcile the SDK observation before admission.
            bounds.CompleteSdkTool(tool);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _unresolved = true; }
        finally { _gate.Release(); }
    }

    private static JsonObject Receipt(Entry action, string state, string? observed) => new()
    {
        ["session_id"] = action.Session, ["tool_call_id"] = action.Id, ["tool"] = action.Tool, ["action_hash"] = action.Signature, ["generation"] = action.Generation,
        ["observation"] = action.Observation, ["state"] = state, ["result_hash"] = observed
    };

    private static string Identity(string? first, string? second) => Hash(new JsonArray(first, second).ToJsonString());
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Fingerprint(object? value)
    {
        var element = CopilotProjectFileTool.Arguments(value);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Write(element, writer);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }
    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Write(property.Value, writer); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(item, writer);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
