using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class ProgressiveRules
{
    internal static string ProductionCommit => MissionCampaign.Current is null ? "4941985d0d41b8a4e8d9596621a8f202dd2bfa86" : MissionCampaign.ValidatedProductionCommit;
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

    internal static async Task HydrateModelAsync(LLMRuntimeOptionsStore options, IKeyVaultRuntimeConfigStore vault, IUserConfigRepository userConfigs, CancellationToken ct)
    {
        options.ReplaceRuntimeOptions(await vault.BuildEffectiveOptionsAsync(options.Current, ct));
        var saved = await userConfigs.GetAsync(ct: ct);
        foreach (var entry in saved.ModelOverrides ?? new Dictionary<string, GnOuGo.AI.Core.LLMModelMetadata>())
            options.UpsertModelOverride(entry.Key, entry.Value);
        if (!string.IsNullOrWhiteSpace(saved.DefaultLlmProvider) && !options.SetDefaultProvider(saved.DefaultLlmProvider, saved.DefaultLlmModel))
            throw new InvalidOperationException("The saved default model provider is not configured.");
    }

    internal static void RequireStart(JsonArray stages, int stage, int maximumStage = 3)
    {
        if (stage is < 1 or > 3) throw new InvalidOperationException("Only three staged starts are authorized.");
        if (stage > maximumStage) throw new InvalidOperationException("This campaign does not authorize the requested stage.");
        if (stages[stage - 1]!["status"]!.ToString() != "not_run") throw new InvalidOperationException("This stage already owns its single start reservation.");
        if (stage > 1 && stages[stage - 2]!["status"]!.ToString() != "passed") throw new InvalidOperationException("The previous stage has not passed its gate.");
        if (stage == 3 && stages[1]!["outcome"]?.ToString() != "valid_workflow") throw new InvalidOperationException("Stage 2 must produce ValidWorkflow before CodeReview.");
    }

    internal static bool ShouldAdvance(string command) => command is "start" or "advance";

    // Independent checks for the frozen Stage-1 fixture only; never planner inference.
    internal static string? SourceText(PlanningSnapshot state, string reference)
    {
        var matches = state.References.Where(r => r.Id == reference).ToArray();
        if (matches.Length != 1) return null;
        var r = matches[0]; var source = state.Request.Prompt;
        if (r.SourceId != "request" || r.Owner != state.Request.TenantId + ":" + state.Request.SessionId ||
            r.SourceRevision > state.Revision || r.SourceFingerprint != PlanningGraphCompiler.Fingerprint(source) ||
            r.Start < 0 || r.Length < 1 || r.Start > source.Length - r.Length) return null;
        return source.Substring(r.Start, r.Length);
    }

    internal static string? PublicName(PlanningSnapshot state, PlanningBusinessDeclaration declaration)
    {
        var text = SourceText(state, declaration.NameReference);
        if (text?.StartsWith('"') == true)
        {
            try { return JsonNode.Parse(text)?.GetValue<string>(); }
            catch (JsonException) { return null; }
        }
        return text;
    }

    internal static void RequireStageOneOperations(PlanningSnapshot state)
    {
        var actions = state.Obligations.Where(o => o.OperationAdmission is not null).ToArray();
        var rules = "Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.";
        if (string.IsNullOrEmpty(state.OperationAdmissionFingerprint) || actions.Length != 1 || actions.Any(o => o.Kind != "local_processing" ||
            o.Disposition != "admitted" || o.OperationAdmission is not { Version: 18 } || o.OperationAdmission.CanonicalId != o.Id ||
            o.Grounding?.Authority != PlanningSourceAuthority.RequestedBehavior) ||
            !actions.Any(o => o.Required && o.OperationAdmission!.Assignments.Any(a => SourceText(state, a.ClauseReference) == rules)) ||
            state.BehaviorPlan is null || actions.Where(o => o.Required).Any(o => !state.BehaviorPlan.Workflows.Any(w =>
                PlanningBehaviorPlans.Enumerate(w.Steps.Concat(w.Finally)).Any(n => n.OperationIds.Contains(o.Id)))))
            throw new InvalidOperationException("STAGE1_OPERATION_REVIEW_MISMATCH");
        foreach (var action in actions) RuntimeAdmissionDiagnosticRules.RequirePositiveSupports(action);
    }

    internal static void RequireStageOneDeclarations(PlanningSnapshot state)
    {
        void Require(bool condition) { if (!condition) throw new InvalidOperationException("STAGE1_DECLARATION_REVIEW_MISMATCH"); }
        Require(state.Declarations.Count == 3 && !string.IsNullOrEmpty(state.DeclarationFingerprint));
        var expected = new[] { (Name: "record", Direction: "input", Required: true),
            (Name: "threshold", Direction: "input", Required: false), (Name: "classifiedResult", Direction: "output", Required: true) };
        Require(state.BehaviorPlan?.Workflows.Count == 1);
        var workflow = state.BehaviorPlan!.Workflows[0];
        Require(workflow.Inputs.Count == 2 && workflow.Outputs.Count == 1);
        foreach (var item in expected)
        {
            var matches = state.Declarations.Where(d => PublicName(state, d) == item.Name).ToArray();
            Require(matches.Length == 1);
            var declaration = matches[0];
            Require(declaration.Direction == item.Direction && declaration.Required == item.Required && !string.IsNullOrEmpty(declaration.ProofFingerprint));
            var ports = (item.Direction == "input" ? workflow.Inputs : workflow.Outputs).Where(p => p.Name == item.Name).ToArray();
            Require(ports.Length == 1 && ports[0].DeclarationId == declaration.Id && ports[0].Required == item.Required);
            if (item.Name == "threshold")
            {
                Require(declaration.DefaultReference is not null && SourceText(state, declaration.DefaultReference) == "100");
                Require(declaration.ClauseReferences.Any(r => SourceText(state, r)?.Contains("non-nullable number defaulting to 100 when omitted", StringComparison.Ordinal) == true));
            }
            else Require(declaration.DefaultReference is null);
            if (item.Name == "classifiedResult")
            {
                Require(declaration.ClauseReferences.Any(r => SourceText(state, r)?.Contains("category has exactly the values rejected, high, standard", StringComparison.Ordinal) == true));
            }
        }
    }

    // Campaign observation only. Never changes a graph, assignment, request or planner budget.
    internal static void ObserveThresholdRepair(JsonObject stage, PlanningSnapshot state, IReadOnlyDictionary<string, LLMResponse?> receipts)
    {
        if (stage["status"]?.ToString() == "blocked") return;
        void Block(string code, string classification)
        {
            stage["status"] = "blocked"; stage["failureCode"] = code;
            stage["harnessFailure"] = classification; stage["revision"] = state.Revision;
        }
        if (state.TechnicalStop?.Unverifiable == true || state.RequestAccounting.Any(c => c.Evidence == "unverifiable"))
        { Block(state.TechnicalStop?.Code ?? "MODEL_REQUEST_UNVERIFIABLE", "UnverifiableRequest"); return; }

        var findings = state.Construction.Candidates.SelectMany(candidate => candidate.Diagnostics
            .Where(d => d.Required && d.Code == "BUSINESS_INPUT_BINDING_MISSING" && d.Rule == "input:threshold")
            .SelectMany(d => candidate.Targets.Where(h => d.Location == "/assignments/" + h.Id || d.Location.StartsWith("/assignments/" + h.Id + "/", StringComparison.Ordinal))
                .Select(h => (candidate.WorkflowKey, Location: h.CanonicalLocation))))
            .Distinct().OrderBy(f => f.WorkflowKey, StringComparer.Ordinal).ThenBy(f => f.Location, StringComparer.Ordinal).ToArray();
        if (stage["thresholdRepair"] is not JsonObject watch)
        {
            if (findings.Length == 0) return;
            var finding = findings[0];
            stage["thresholdRepair"] = new JsonObject
            {
                ["status"] = "awaiting_repair", ["workflow"] = finding.WorkflowKey, ["location"] = finding.Location,
                ["code"] = "BUSINESS_INPUT_BINDING_MISSING", ["rule"] = "input:threshold", ["observedRevision"] = state.Revision,
                ["priorAttempts"] = state.Attempts.Count,
                ["priorRequests"] = new JsonArray(state.RequestAccounting.Select(c => (JsonNode?)JsonValue.Create(c.Id)).ToArray())
            };
            return; // The initial finding is eligible for the existing repair, not a campaign stop.
        }
        if (watch["status"]?.ToString() != "awaiting_repair") return;
        var prior = watch["priorRequests"]!.AsArray().Select(v => v!.ToString()).ToHashSet(StringComparer.Ordinal);
        var calls = state.RequestAccounting.Where(c => c.WorkflowKey == watch["workflow"]!.ToString() && c.Phase == PlanningPhase.Repair && !prior.Contains(c.Id)).ToArray();
        var remaining = findings.Any(f => f.WorkflowKey == watch["workflow"]!.ToString() && f.Location == watch["location"]!.ToString());
        if (calls.Length == 0)
        {
            if (!remaining && state.TechnicalStop is null) { watch["status"] = "resolved_without_model"; watch["assessedRevision"] = state.Revision; }
            return;
        }
        watch["requests"] = new JsonArray(calls.Select(c => (JsonNode?)JsonValue.Create(c.Id)).ToArray());
        if (calls.Any(c => !receipts.TryGetValue(c.Id, out var receipt) || receipt is null))
        { Block("BENCHMARK_REPAIR_RECEIPT_UNAVAILABLE", "UnverifiableRequest"); return; }
        if (calls.Any(c => state.Construction.PendingCalls.Any(p => p.Id == c.Id))) return; // A verified receipt still needs normal planner assessment.
        if (!calls.Any(c => receipts[c.Id]!.CompletionStatus != "output_limit")) return;
        var rejected = state.Attempts.Skip(watch["priorAttempts"]!.GetValue<int>()).Any(a => a.Phase == PlanningPhase.Repair && !a.Retained);
        watch["assessedRevision"] = state.Revision;
        if (remaining || rejected)
        {
            watch["status"] = "failed_with_receipt";
            Block("BENCHMARK_THRESHOLD_REPAIR_FAILED", "PlannerModelConvergence");
        }
        else watch["status"] = "resolved";
    }

    internal static void RequireOpen(JsonObject stage)
    {
        if (stage["status"]?.ToString() is "blocked" or "accepted_behavior" or "passed")
            throw new InvalidOperationException("This campaign has reached its stopping checkpoint. Only inspection or offline replay is permitted.");
    }

    internal static void RecordBehaviorCheckpoint(JsonObject stage, PlanningSnapshot state, string reviewedHash, int priorRequests)
    {
        if (state.TechnicalStop is not null || state.Status != PlanningStatus.Generating || state.Graph is null ||
            state.BehaviorPlan is null || state.ApprovedBehaviorHash != reviewedHash ||
            PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan) != reviewedHash || state.Construction.Dataflow is not null ||
            state.RequestAccounting.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != priorRequests ||
            state.Construction.Holes.Any(h => h.ExposedRequests.Count != 0))
            throw new InvalidOperationException("Behavior acceptance must preserve the exact review and create only its deterministic skeleton.");
        stage["status"] = "behavior_accepted"; stage["acceptedBehaviorHash"] = reviewedHash;
        stage["skeletonHash"] = PlanningGraphCompiler.Fingerprint(state.Graph);
    }

    internal static bool RecoverBehaviorCheckpoint(JsonObject stage, PlanningSnapshot state)
    {
        if (stage["acceptedBehaviorHash"] is { } recorded)
        {
            if (recorded.ToString() != state.ApprovedBehaviorHash)
                throw new InvalidOperationException("The accepted behavior differs from the recorded checkpoint.");
            return false;
        }
        if (state.ApprovedBehaviorHash is not { } accepted) return false;
        RecordBehaviorCheckpoint(stage, state, accepted, state.RequestAccounting.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        return true;
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
