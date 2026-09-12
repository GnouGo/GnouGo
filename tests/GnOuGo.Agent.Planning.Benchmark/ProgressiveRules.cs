using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class ProgressiveRules
{
    internal const string ProductionCommit = "ee487c8d998721368b20c89653526c919a70f881";
    internal static TypedWorkflowPlanningSettings Settings() => new()
    {
        BackgroundProcessingEnabled = false,
        ReasoningProfile = new() { Routine = "low", Behavior = "low", SemanticReview = "low" }
    };

    internal static void RequirePreflight(JsonObject? manifest)
    {
        if (manifest?["preflightStop"] is not null)
            throw new InvalidOperationException("The campaign stopped during preflight; no provider request or session start is permitted.");
    }

    internal static void RequireStart(JsonArray stages, int stage)
    {
        if (stage is < 1 or > 3) throw new InvalidOperationException("Only three staged starts are authorized.");
        if (stages[stage - 1]!["status"]!.ToString() != "not_run") throw new InvalidOperationException("This stage already owns its single start reservation.");
        if (stage > 1 && stages[stage - 2]!["status"]!.ToString() != "passed") throw new InvalidOperationException("The previous stage has not passed its gate.");
        if (stage == 3 && stages[1]!["outcome"]?.ToString() != "valid_workflow") throw new InvalidOperationException("Stage 2 must produce ValidWorkflow before CodeReview.");
    }

    internal static bool CanPass(int stage, PlanningSnapshot state, bool justified)
        => state.TechnicalStop is null && (state.Outcome switch
        {
            PlanningValidWorkflow valid => state.Status == PlanningStatus.Approved && valid.ArtifactHash == state.ArtifactHash && state.ApprovedHash == state.ArtifactHash,
            PlanningNeedUserClarification choice => stage == 1 && justified && state.Status == PlanningStatus.Clarification && choice.Decision.EvidenceReferences.Count > 0,
            PlanningUnsupported unavailable => stage == 1 && justified && state.Status == PlanningStatus.Unsupported && unavailable.Obligations.Count > 0 && unavailable.Obligations.All(o => o.EvidenceReferences.Count > 0),
            _ => false
        });

    internal static void RequireReview(PlanningSnapshot state, long revision, string hash, string status)
    {
        if (state.Revision != revision || state.ArtifactHash != hash || state.Status != status || state.TechnicalStop is not null)
            throw new InvalidOperationException("Review requires the exact current revision, artifact hash and waiting state.");
    }

    internal static void RequireApproval(PlanningSnapshot state, long revision, string hash, JsonArray? results, string fixtureHash, string catalogHash, IReadOnlyList<string> cases)
    {
        RequireReview(state, revision, hash, PlanningStatus.FinalReview);
        if (results is null || results.Count != cases.Count || !results.Select(r => r?["case"]?.ToString()).Order(StringComparer.Ordinal).SequenceEqual(cases.Order(StringComparer.Ordinal)) ||
            results.Any(r => r?["passed"]?.GetValue<bool>() != true || r["artifactHash"]?.ToString() != hash || r["fixtureHash"]?.ToString() != fixtureHash || r["catalogHash"]?.ToString() != catalogHash))
            throw new InvalidOperationException("Approval requires every independent fixture for this exact artifact, fixture and catalog hash.");
    }

    internal static bool EligibleAb(PlanningSnapshot state, PlanningDecisionPage page, LLMRequest request, LLMResponse? response, bool used)
        => !used && state.TechnicalStop is not null && !state.TechnicalStop.Unverifiable &&
           page.RequestId is not null && state.RequestAccounting.LastOrDefault(c => c.Evidence == "receipt")?.Id == page.RequestId &&
           !new[] { "PROVIDER", "LLM_", "INPUT_LIMIT", "OUTPUT_LIMIT", "SIZE", "CONTRACT_UNRESOLVED", "SCHEMA" }.Any(s => state.TechnicalStop.Code.Contains(s, StringComparison.Ordinal)) &&
           (page.Phase.StartsWith("behavior", StringComparison.Ordinal) || page.Phase.StartsWith("semantic_review", StringComparison.Ordinal)) &&
           page.Decisions.Count == 1 && page.EstimatedInputTokens <= 2400 && page.EstimatedAnswerTokens <= 512 &&
           request.Reasoning == "low" && response is { CompletionStatus: not "output_limit", Json: JsonObject } &&
           PlanningContractValidation.ValidateInstance(response.Json, request.StructuredOutputSchema!).Count == 0;

    internal static LLMRequest DiagnosticRequest(PlanningSnapshot state, PlanningDecisionPage page, LLMRequest source, LLMResponse? receipt, bool used)
    {
        if (!EligibleAb(state, page, source, receipt, used)) throw new InvalidOperationException("This failure is ineligible for the single bounded semantic A/B diagnostic.");
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(source, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        request.Reasoning = "medium"; request.ClientRequestId = null;
        request.ClientRequestId = state.Request.SessionId + ":diagnostic-medium:" + page.Id + ":" + GnOuGo.Flow.Planning.PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        return request;
    }
}
