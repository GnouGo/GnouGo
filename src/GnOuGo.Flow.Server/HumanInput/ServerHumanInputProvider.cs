using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Server.HumanInput;

/// <summary>
/// HTTP-based human input provider for GnOuGo.Flow.Server.
/// Uses a <see cref="TaskCompletionSource{T}"/> per pending request,
/// keyed by "{runId}:{stepId}". The matching HTTP endpoint resolves
/// the TCS when the user submits a response.
/// </summary>
public sealed class ServerHumanInputProvider : IHumanInputProvider
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();

    public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
    {
        var key = $"{request.RunId}:{request.StepId}";
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        ct.Register(() =>
        {
            if (_pending.TryRemove(key, out var removed))
                removed.TrySetCanceled(ct);
        });

        return tcs.Task;
    }

    /// <summary>
    /// Called by the HTTP endpoint when the user submits a response.
    /// Returns true if a matching pending request was found and resolved.
    /// </summary>
    public bool TrySubmitResponse(string runId, string stepId, JsonNode? response)
    {
        var key = $"{runId}:{stepId}";
        if (_pending.TryRemove(key, out var tcs))
            return tcs.TrySetResult(response);
        return false;
    }

    /// <summary>
    /// Returns true if there is a pending request for the given run+step.
    /// </summary>
    public bool HasPending(string runId, string stepId) => _pending.ContainsKey($"{runId}:{stepId}");

    /// <summary>
    /// Returns all currently pending request keys.
    /// </summary>
    public IReadOnlyCollection<string> PendingKeys => _pending.Keys.ToList().AsReadOnly();
}


