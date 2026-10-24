using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Captured, host-journaled retries of confirmed transient generation responses.</summary>
public sealed class PlanningTransportPolicy
{
    public int Version { get; set; } = 1;
    public int MaxAttempts { get; set; } = 3;
    public int MaxUncertainRetries { get; set; }
    public int AttemptTimeoutMilliseconds { get; set; } = 600_000;
    public int BaseDelayMilliseconds { get; set; } = 1_000;
    public int MaxDelayMilliseconds { get; set; } = 30_000;
    public int MaxTotalDelayMilliseconds { get; set; } = 60_000;
}

/// <summary>Absence preserves historical request preparation and recovery.</summary>
public static class PlanningTransportPolicies
{
    public const string OptionName = "planning_transport_policy";

    public static PlanningTransportPolicy? Read(PlanningRequest request)
    {
        if (!request.Options.TryGetPropertyValue(OptionName, out var node)) return null;
        if (node is not JsonObject policy || new[] { "version", "maxAttempts", "maxUncertainRetries",
            "attemptTimeoutMilliseconds", "baseDelayMilliseconds", "maxDelayMilliseconds", "maxTotalDelayMilliseconds" }
            .Any(key => policy[key] is not JsonValue value || !value.TryGetValue<int>(out _)))
            throw new PlanningConflictException("The captured planning transport policy is incomplete.");
        PlanningTransportPolicy result;
        try { result = policy.Deserialize(PlanningJsonContext.Default.PlanningTransportPolicy)!; }
        catch (JsonException) { throw new PlanningConflictException("The captured planning transport policy is invalid."); }
        Validate(result);
        return result;
    }

    public static void Validate(PlanningTransportPolicy policy)
    {
        if (policy.Version != 1 || policy.MaxAttempts is < 1 or > 3 || policy.MaxUncertainRetries != 0 ||
            policy.AttemptTimeoutMilliseconds <= 0 || policy.BaseDelayMilliseconds <= 0 ||
            policy.MaxDelayMilliseconds < policy.BaseDelayMilliseconds || policy.MaxTotalDelayMilliseconds < 0)
            throw new PlanningConflictException("Unsupported or invalid confirmed-response planning transport policy.");
    }

    /// <summary>Called only at fresh creation; never fills absent settings on a restored session.</summary>
    public static PlanningTransportPolicy Capture(PlanningRequest request, PlanningTransportPolicy? policy = null)
    {
        var existing = Read(request);
        if (existing is not null)
        {
            if (policy is not null && !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(existing, PlanningJsonContext.Default.PlanningTransportPolicy),
                JsonSerializer.SerializeToNode(policy, PlanningJsonContext.Default.PlanningTransportPolicy)))
                throw new PlanningConflictException("The captured planning transport policy cannot change.");
            return existing;
        }
        policy ??= new();
        Validate(policy);
        request.Options[OptionName] = JsonSerializer.SerializeToNode(policy, PlanningJsonContext.Default.PlanningTransportPolicy);
        return Read(request)!;
    }

    internal static LLMRequest Apply(LLMRequest request, PlanningRequest planning, bool explicitlyDisabled)
    {
        if (Read(planning) is null) return request;
        request.DisableTransportRetries = explicitlyDisabled;
        request.UseBackgroundMode = false;
        return request;
    }

    public static void ValidateIssued(LLMRequest request, PlanningRequest planning)
    {
        if (Read(planning) is not null && (request.UseBackgroundMode || request.Tools is { Count: > 0 } ||
            request.RequiredReviewCalls is not (>= 0 and <= 2)))
            throw new PlanningConflictException("Journaled planning transport requires synchronous tool-free generation and captured review headroom.");
    }
}
