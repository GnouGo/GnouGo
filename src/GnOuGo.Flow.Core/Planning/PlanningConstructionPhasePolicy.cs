using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Immutable creation settings for the simplified construction protocol.</summary>
public sealed class PlanningConstructionPhasePolicy
{
    public int Version { get; set; } = 1;
    public PlanningPhaseSettings Navigation { get; set; } = new() { TargetInputTokens = 3_000, MaxInputTokens = 6_000 };
    public PlanningPhaseSettings Draft { get; set; } = new() { TargetInputTokens = 8_000, MaxInputTokens = 16_000 };
    public PlanningPhaseSettings Review { get; set; } = new() { TargetInputTokens = 4_000, MaxInputTokens = 8_000, OutputTokens = 2_048 };
}

public sealed class PlanningPhaseSettings
{
    public int TargetInputTokens { get; set; }
    public int MaxInputTokens { get; set; }
    public string Reasoning { get; set; } = "medium";
    public int OutputTokens { get; set; } = 8_192;
}

/// <summary>One captured policy resolves planner, host and transport admission.</summary>
public static class PlanningConstructionPhasePolicies
{
    public static PlanningConstructionPhasePolicy? Read(PlanningRequest planning)
    {
        if (planning.Options["construction_limits"] is not JsonObject limits ||
            !limits.TryGetPropertyValue("phasePolicy", out var node)) return null;
        if (node is not JsonObject policy ||
            policy["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1)
            throw new PlanningConflictException("The captured construction phase policy is malformed or unsupported.");
        foreach (var key in new[] { "navigation", "draft", "review" })
        {
            if (policy[key] is not JsonObject phase ||
                phase["targetInputTokens"] is not JsonValue target || !target.TryGetValue<int>(out _) ||
                phase["maxInputTokens"] is not JsonValue maximum || !maximum.TryGetValue<int>(out _) ||
                phase["outputTokens"] is not JsonValue output || !output.TryGetValue<int>(out _) ||
                phase["reasoning"] is not JsonValue reasoning || !reasoning.TryGetValue<string>(out _))
                throw new PlanningConflictException("The captured construction phase policy is incomplete.");
        }
        var result = policy.Deserialize(PlanningJsonContext.Default.PlanningConstructionPhasePolicy)!;
        Validate(result, PlanningTokenThresholds.OptimizationAdvisory(planning));
        return result;
    }

    public static void Validate(PlanningConstructionPhasePolicy policy) => Validate(policy, advisoryOptimization: false);

    public static void Validate(PlanningConstructionPhasePolicy policy, bool advisoryOptimization)
    {
        if (policy.Version != 1) throw new PlanningConflictException("Unsupported construction phase policy version.");
        foreach (var phase in new[] { policy.Navigation, policy.Draft, policy.Review })
        {
            if (phase is null || phase.TargetInputTokens <= 0 || phase.TargetInputTokens > phase.MaxInputTokens)
                throw new PlanningConflictException("Construction input targets must be positive and no greater than their configured maxima.");
            PlanningGenerationPolicy.Validate(new()
            {
                MaxInputTokensPerRequest = phase.MaxInputTokens, MaxOutputTokens = phase.OutputTokens, Reasoning = phase.Reasoning
            }, advisoryOptimization);
        }
    }

    public static PlanningPhaseSettings Resolve(PlanningRequest planning, string purpose)
    {
        var policy = Read(planning) ?? throw new PlanningConflictException("The captured construction phase policy is required.");
        var advisoryOptimization = PlanningTokenThresholds.OptimizationAdvisory(planning);
        PlanningGenerationPolicy.Validate(planning.Generation, advisoryOptimization);
        var selected = purpose switch
        {
            "construction_intent" or "construction_select" or "construction_implementation" or "construction_discovery" => policy.Navigation,
            "construction_draft" or "replan" => policy.Draft,
            "construction_local_judge" or "construction_global_judge" => policy.Review,
            _ => throw new PlanningConflictException("The construction request has no recognized phase policy.")
        };
        if (advisoryOptimization)
            return new() { TargetInputTokens = selected.TargetInputTokens, MaxInputTokens = selected.MaxInputTokens,
                Reasoning = selected.Reasoning, OutputTokens = selected.OutputTokens };
        var maximum = Math.Min(selected.MaxInputTokens, planning.Generation.MaxInputTokensPerRequest);
        return new() { TargetInputTokens = Math.Min(selected.TargetInputTokens, maximum), MaxInputTokens = maximum,
            Reasoning = selected.Reasoning, OutputTokens = Math.Min(selected.OutputTokens, planning.Generation.MaxOutputTokens) };
    }

    public static LLMRequest Apply(LLMRequest request, PlanningRequest planning, string purpose)
    {
        var explicitlyDisabled = request.DisableTransportRetries;
        var phase = Resolve(planning, purpose);
        // The phase reservation replaces a caller's legacy default, before hashing or dispatch.
        request.MaxTokens = phase.OutputTokens;
        return PlanningTransportPolicies.Apply(PlanningGenerationPolicy.Apply(request, new()
        {
            MaxInputTokensPerRequest = phase.MaxInputTokens, MaxOutputTokens = phase.OutputTokens, Reasoning = phase.Reasoning
        }, PlanningTokenThresholds.OptimizationAdvisory(planning)), planning, explicitlyDisabled);
    }

    public static void ValidateIssued(LLMRequest request, PlanningRequest planning)
    {
        PlanningTransportPolicies.ValidateIssued(request, planning);
        var phase = Resolve(planning, request.StructuredOutputSchema?["title"]?.ToString() ?? "");
        if (request.Reasoning != phase.Reasoning || request.MaxTokens != phase.OutputTokens ||
            !request.RequireOutputTokenLimit || PlanningTransportPolicies.Read(planning) is null && !request.DisableTransportRetries)
            throw new PlanningConflictException("The issued request differs from its captured construction phase policy.");
    }

    public static PlanningConstructionPhasePolicy Capture(PlanningRequest request, PlanningConstructionPhasePolicy policy)
    {
        var advisoryOptimization = PlanningTokenThresholds.OptimizationAdvisory(request);
        Validate(policy, advisoryOptimization);
        PlanningGenerationPolicy.Validate(request.Generation, advisoryOptimization);
        var clone = JsonSerializer.SerializeToNode(policy, PlanningJsonContext.Default.PlanningConstructionPhasePolicy)!
            .Deserialize(PlanningJsonContext.Default.PlanningConstructionPhasePolicy)!;
        if (advisoryOptimization) return clone;
        foreach (var phase in new[] { clone.Navigation, clone.Draft, clone.Review })
        {
            phase.MaxInputTokens = Math.Min(phase.MaxInputTokens, request.Generation.MaxInputTokensPerRequest);
            phase.TargetInputTokens = Math.Min(phase.TargetInputTokens, phase.MaxInputTokens);
            phase.OutputTokens = Math.Min(phase.OutputTokens, request.Generation.MaxOutputTokens);
        }
        return clone;
    }
}
