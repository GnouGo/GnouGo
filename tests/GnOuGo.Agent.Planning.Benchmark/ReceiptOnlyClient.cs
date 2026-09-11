using System.Text.Json;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

// Offline test transport. There is deliberately no provider or journal fallback.
internal sealed class ReceiptOnlyClient(string session, IReadOnlyDictionary<string, (LLMRequest Request, LLMResponse? Response)> evidence) : ILLMClient
{
    internal HashSet<string> Replayed { get; } = new(StringComparer.Ordinal);

    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = request.ClientRequestId;
        if (id is null || !id.StartsWith(session + ":", StringComparison.Ordinal) || !evidence.TryGetValue(id, out var captured))
            throw new WorkflowRuntimeException("REPLAY_EVIDENCE_REQUIRED", "No captured request exists for this exact reservation. Offline replay cannot dispatch it.");
        var json = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var copy = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.LLMRequest)!;
        copy.ClientRequestId = null;
        var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(copy, PlanningJsonContext.Default.LLMRequest));
        if (!id.EndsWith(":" + hash, StringComparison.Ordinal) || json != JsonSerializer.Serialize(captured.Request, PlanningJsonContext.Default.LLMRequest))
            throw new WorkflowRuntimeException("REPLAY_REQUEST_CHANGED", "The request differs from its captured reservation. Offline replay cannot substitute a receipt.");
        if (captured.Response is null)
            throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetUnverifiable, "The captured reservation has no receipt. Offline replay cannot dispatch it.");
        Replayed.Add(id);
        // Preserve output-limit and invalid candidates for the production validators.
        return Task.FromResult(JsonSerializer.Deserialize(JsonSerializer.Serialize(captured.Response, PlanningJsonContext.Default.LLMResponse), PlanningJsonContext.Default.LLMResponse)!);
    }
}
