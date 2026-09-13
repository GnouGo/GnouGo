using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

// Offline test transport. There is deliberately no provider or journal fallback.
internal sealed class ReceiptOnlyClient(string session, IReadOnlyDictionary<string, (LLMRequest Request, LLMResponse? Response)> evidence,
    JsonObject? frozenModel = null) : ILLMClient, ILLMCapabilityResolver
{
    internal HashSet<string> Replayed { get; } = new(StringComparer.Ordinal);

    // Only the campaign's archived metadata proves preflight support. A receipt
    // alone is not a declaration of model capabilities, and no discovery runs here.
    public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>?>(Matches(provider, model) && frozenModel?["reasoningLevels"] is JsonArray levels
            ? levels.Select(v => v!.ToString()).ToArray() : null);
    }
    public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Matches(provider, model) ? frozenModel?["structuredOutput"]?.GetValue<bool>() : null);
    }
    private bool Matches(string? provider, string model) => frozenModel is not null &&
        (provider is null || provider == frozenModel["provider"]?.ToString()) && model == frozenModel["model"]?.ToString();

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
