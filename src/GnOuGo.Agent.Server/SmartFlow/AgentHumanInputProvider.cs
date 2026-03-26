using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.SmartFlow;

/// <summary>
/// Blazor-friendly <see cref="IHumanInputProvider"/> that bridges the
/// GnOuGo.Flow workflow engine with the interactive Blazor UI.
///
/// When the workflow hits a <c>human.input</c> step:
///   1. The request is written to <see cref="PendingRequests"/> channel.
///   2. The workflow blocks on a <see cref="TaskCompletionSource{T}"/>.
///   3. The UI reads from the channel, renders the prompt/choices/fields.
///   4. The UI calls <see cref="TrySubmitResponse"/> to unblock the workflow.
/// </summary>
public sealed class AgentHumanInputProvider : IHumanInputProvider
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Channel<HumanInputRequest> _requestChannel;

    public AgentHumanInputProvider()
    {
        _requestChannel = Channel.CreateUnbounded<HumanInputRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Channel reader that the Blazor UI consumes to display pending human input requests.
    /// </summary>
    public ChannelReader<HumanInputRequest> PendingRequests => _requestChannel.Reader;

    public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
    {
        var key = $"{request.RunId}:{request.StepId}";
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        // Write the request to the channel so the UI can pick it up
        _requestChannel.Writer.TryWrite(request);

        ct.Register(() =>
        {
            if (_pending.TryRemove(key, out var removed))
                removed.TrySetCanceled(ct);
        });

        return tcs.Task;
    }

    /// <summary>
    /// Called by the Blazor UI when the user submits a response.
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
    public bool HasPending(string runId, string stepId) =>
        _pending.ContainsKey($"{runId}:{stepId}");
}

