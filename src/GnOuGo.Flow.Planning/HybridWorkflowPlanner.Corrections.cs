using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    // Private, bounded bookkeeping in the existing session journal. No new authority
    // is inferred for old sessions or for an already-issued request.
    private const string CorrectionProgressKey = "automatic_correction_progress";
    private static bool AutomaticCorrection(PlanningSession state) =>
        state.Request.Options["automatic_correction"] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;

    private static string CorrectionCode(PlanningDiagnostic diagnostic) => diagnostic.Code == "CONTRACT_UNSATISFIED" &&
        diagnostic.ValidationStage?.StartsWith("contract:", StringComparison.Ordinal) == true ? diagnostic.ValidationStage[9..] : diagnostic.Code;
    private static bool Correctable(PlanningDiagnostic diagnostic) => CorrectionCode(diagnostic) is
        "TASK_FIELD_UNKNOWN" or "TASK_FIELD_TYPE" or "TASK_FIELD_INVALID" or "TASK_REFERENCE_UNKNOWN" or "TASK_OUTPUT_UNKNOWN" or
        "TASK_CONDITION_TYPE" or "TASK_PREDICATE_INVALID" or "TASK_PREDICATE_TYPE" or "TASK_PRESENCE_SCOPE" or
        "TASK_TRANSFORM_EACH" or "TASK_TRANSFORM_INPUT" or "TASK_TRANSFORM_MODE" or "TASK_TRANSFORM_TYPE" or "TASK_TRANSFORM_CONSTRAINT" or
        "TASK_INPUT_TYPE" or "TASK_INPUT_REQUIRED" or "TASK_INPUT_UNKNOWN" or "TASK_INPUT_BINDING" or "TASK_INPUT_CONSTRAINT" or "TASK_INPUT_CONFLICT" or
        "TASK_LOOKUP_INVALID" or "TASK_FLATTEN_INVALID" or "TASK_ITEMS_INVALID" or "TASK_BRANCH_OUTPUTS" or "TASK_EXPORT_REQUIRED" or
        "TASK_DEPENDENCY_UNKNOWN" or "TASK_DEPENDENCY_CYCLE" or "TASK_GROUP_INPUTS" or "TASK_GROUP_INPUT_TYPE" or "TASK_GROUP_UNKNOWN" or
        "TASK_KIND_INVALID" or "TASK_OPERATION_UNKNOWN" or "TASK_ARTIFACT_BINDING" or "TASK_ARTIFACT_PREREQUISITE_MISSING" or "TASK_OUTPUT_CONTRACT" or
        "CHOICE_UNKNOWN" or "CHOICE_SLOT_INVALID" or "CHOICE_INVALID" or "PLANNING_RESPONSE_INVALID" or "PROPOSAL_ACTION_INVALID" or
        "REVISION_REQUIRED" or "REVISION_SCOPE_CHANGED" or "REPAIR_SCOPE_INVALID" or "REPLAN_NO_PROGRESS" or
        "REQUIREMENTS_CHANGED" or "REQUIREMENTS_INPUTS_CHANGED" or "REQUIREMENTS_OUTPUTS_CHANGED" or "RUNTIME_COMPOSITION_FAILURE";

    private static bool InitialProposalDefect(PlanningDiagnostic diagnostic) => diagnostic.Code == "PLANNING_RESPONSE_INVALID" ||
        diagnostic.Code is "REQUIREMENTS_INVALID" or "REQUIREMENTS_INPUTS_REQUIRED" or "REQUIREMENTS_OUTPUTS_REQUIRED" &&
        (diagnostic.Location == "/requirements" || diagnostic.Location.StartsWith("/requirements/", StringComparison.Ordinal));

    private static bool CanCorrect(PlanningSession state) => AutomaticCorrection(state) && state.PendingCall is null &&
        state.ApprovedHash is null &&
        PlanningModelCalls.RemainingCalls(state) > 0 && PlanningModelCalls.RemainingRepairs(state) > 0 &&
        state.Diagnostics.Any(d => d.Required) && (state.Requirements is not null
            ? state.Diagnostics.Where(d => d.Required).All(Correctable)
            : state.Plan is null && state.Request.Baseline is null && state.Diagnostics.Where(d => d.Required).All(InitialProposalDefect));

    private static void ContinueAutomaticCorrection(PlanningSession state)
    {
        if (state.Status != PlanningStatus.Stopped || !CanCorrect(state)) return;
        if (state.Request.Options[CorrectionProgressKey]?["action"]?.ToString() == "regenerate") return;
        state.Status = PlanningStatus.Generating;
    }

    private static void RecordCorrectionOutcome(PlanningSession state)
    {
        if (state.PendingCall is not null || state.Request.Options[CorrectionProgressKey] is not JsonObject progress ||
            progress["attempts"] is not JsonArray { Count: > 0 } attempts || attempts[^1] is not JsonObject attempt ||
            state.ModelCalls <= attempt["callsUsed"]!.GetValue<int>() || attempt.ContainsKey("completedAtCall")) return;
        attempt["completedAtCall"] = state.ModelCalls;
        attempt["outcome"] = state.Status == PlanningStatus.FinalReview ? "validated" :
            state.Status == PlanningStatus.Clarification ? "clarification_required" : "rejected";
        var candidate = progress["candidate"]?.ToString();
        if (candidate is not null) attempt["responseCandidate"] = candidate;
        if (candidate is not null && state.Diagnostics.Any(d => d.Required) && attempts.Any(a => a?["candidate"]?.ToString() == candidate ||
            a != attempt && a?["responseCandidate"]?.ToString() == candidate))
        {
            attempt["outcome"] = "repeated_candidate";
            if (!state.Diagnostics.Any(d => d.Code == "REPLAN_NO_PROGRESS"))
                state.Diagnostics.Add(new("REPLAN_NO_PROGRESS", "/", "The correction repeated a previously unsuccessful candidate. No budget was reset."));
        }
        progress.Remove("candidate");
        Activity.Current?.AddEvent(new("planning.correction.result", tags: new ActivityTagsCollection
        { ["action"] = attempt["action"]!.ToString(), ["outcome"] = attempt["outcome"]!.ToString(), ["calls"] = state.ModelCalls, ["replans"] = state.ReplanAttempts }));
    }

    private static bool PrepareAutomaticCorrection(PlanningSession state)
    {
        if (!AutomaticCorrection(state) || state.PendingCall is not null) return true;
        if (state.Plan is null && state.Request.Baseline is { } retained && state.Request.Options[CorrectionProgressKey] is null &&
            PlanningPrompt.FailureContext(state.Request.FailureEvidence) is { Count: > 0 } failure)
        {
            var code = failure["error_code"]?.ToString();
            var mapping = code == "CONTRACT_UNSATISFIED" && failure["mapping_failure_kind"]?.ToString() is "program" or "data" &&
                failure["mapping_failure_reason"]?.ToString() is "ungrounded_scalar" or "missing_scalar";
            if (failure["correction_blocked"]?.GetValue<bool>() == true || !mapping && code is not ("EXPR_TYPE_MISMATCH" or "MCP_REQUEST_EXPR_TYPE_MISMATCH" or "STEP_OUTPUT_PROPERTY_UNKNOWN" or "STEP_REFERENCE_UNKNOWN"))
            { state.Diagnostics = [new(code ?? "EXECUTION_FAILURE", "/", "The retained execution failure is not automatically correctable.")]; Stop(state); return false; }
            var compilation = new TaskPlanCompiler().Compile(retained, state.Catalog!, state.Request);
            var id = failure["failed_step_id"]?.ToString() ?? failure["step_id"]?.ToString();
            var location = id is null ? "/" : compilation.Sources.GetValueOrDefault(id, "/");
            state.Diagnostics = [new("RUNTIME_COMPOSITION_FAILURE", location,
                "Correct the retained execution failure using declared producer values. Preserve observed facts, public contracts and requested operations; never invent a missing scalar.")];
        }
        if (!state.Diagnostics.Any(d => d.Required)) return true;
        if (!CanCorrect(state)) { Stop(state); return false; }
        state.Diagnostics = state.Diagnostics.Select(d => d with { Code = CorrectionCode(d) }).ToList();

        var progress = state.Request.Options[CorrectionProgressKey] as JsonObject;
        if (progress?["action"]?.ToString() == "regenerate") { Stop(state); return false; }
        var baseline = state.Request.Baseline;
        if (progress is null)
        {
            baseline = state.Plan ?? baseline;
            state.Request.Baseline = baseline is null ? null : ClonePlan(baseline);
            progress = new JsonObject { ["version"] = 3, ["attempts"] = new JsonArray(),
                ["outputRemovals"] = new JsonArray((baseline?.Root.Outputs ?? []).Where(o =>
                    state.Requirements?.Outputs is { } accepted && accepted.All(a => a.Name != o.Name) &&
                    state.Diagnostics.Any(d => d.Required && d.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && d.Location == "/outputs/" + o.Name))
                    .Select(o => (JsonNode?)JsonValue.Create(o.Name)).ToArray()),
                ["conditionLocations"] = new JsonArray(state.Diagnostics.Where(d => d.Code == "TASK_CONDITION_TYPE" && d.Location.EndsWith("/requires", StringComparison.Ordinal))
                    .Select(d => d.Location).Distinct().Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["locations"] = new JsonArray(state.Diagnostics.Where(d => d.Required).Select(d => d.Location).Distinct().Order(StringComparer.Ordinal)
                    .Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
            state.Request.Options[CorrectionProgressKey] = progress;
        }
        var findings = state.Diagnostics.ToList();
        if (DirectGuardCorrections(progress) && baseline is not null)
        {
            var guardSources = GuardSources(baseline, state.Catalog!);
            var guards = TaskPlanRevisions.Tasks(baseline).Where(t => t.Requires is not null).ToDictionary(t => "/tasks/" + t.Id + "/requires", t => t.Requires!);
            foreach (var path in progress["conditionLocations"]!.AsArray().Select(p => p!.ToString()))
                if (!guards.TryGetValue(path, out var guard) || !CorrectableGuard(guard, guardSources.GetValueOrDefault(path) ?? []))
                { state.Diagnostics.Add(UnsafeGuard(path)); Stop(state); return false; }
        }
        var attempted = progress["attempts"]!.AsArray();
        var fingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(state.Plan ?? baseline, PlanningJsonContext.Default.TaskPlan));
        var diagnosticKey = string.Join('\n', findings.Where(d => d.Required && d.Code != "REVISION_REQUIRED")
            .Select(d => d.Code + ":" + d.Location + ":" + d.Rule).Distinct().Order(StringComparer.Ordinal));
        var escalate = attempted.Count > 0;
        var action = "patch";
        List<string>? paths = null;
        if (!escalate && state.Plan is not null)
        {
            var immutable = TaskPlanRevisions.UnrepairableRequirements(state).Any();
            if (immutable || state.RevisionScope.Count == 0)
            {
                paths = CorrectionPaths(state, findings);
                action = paths is null ? "regenerate" : "targeted_revision";
            }
        }
        else action = "regenerate";

        if (action != "patch")
        {
            // The same existing revision transition used by explicit commands. The
            // current revision/hash and diagnosed scope are retained as evidence.
            var revision = state.Revision;
            var artifact = state.ComputeArtifactHash();
            PlanningClarifications.Revise(state, preserveRequirements: true, paths);
            state.Request.Baseline = baseline;
            state.Diagnostics = findings;
            state.Request.RevisionContext = "Correct the recorded diagnostics while preserving accepted requirements, public contracts, requested operations, ordered assertions and cleanup. " +
                (paths is null ? "Return a corrected complete TaskPlan. Preserve independent regions and existing operation identities. Never drop work or weaken a check to pass validation." :
                    "Change only the issued editable paths. Preserve guard operands and ordering except the diagnosed invalid references.");
            if (DirectGuardCorrections(progress))
                state.Request.RevisionContext += " " + PlanningPrompt.GuardCorrectionInstructions;
            if (progress["version"]?.ToString() == "3" && progress["outputRemovals"] is JsonArray { Count: > 0 } removals)
                state.Request.RevisionContext += " Remove only the diagnosed unaccepted exports: " + removals.ToJsonString() +
                    ". Keep remaining exports, their bindings and order unchanged. Preserve all operation arguments verbatim; do not rewrite instructions or add replacement exports.";
            progress["revision"] = revision; progress["artifact"] = artifact;
        }
        progress["action"] = action;
        attempted.Add((JsonNode)new JsonObject { ["action"] = action, ["candidate"] = fingerprint,
            ["diagnostics"] = PlanningGraphCompiler.Fingerprint(diagnosticKey), ["callsUsed"] = state.ModelCalls,
            ["outcome"] = escalate ? "targeted_correction_did_not_resolve_findings" : "diagnosed" });
        Activity.Current?.AddEvent(new("planning.correction.selected", tags: new ActivityTagsCollection
        { ["action"] = action, ["reason"] = escalate ? "unresolved_after_targeted_correction" : "diagnostic_authority",
            ["diagnostics"] = string.Join(',', findings.Select(d => d.Code).Distinct()), ["attempt"] = attempted.Count }));
        state.Status = PlanningStatus.Generating;
        return true;
    }

    private static List<string>? CorrectionPaths(PlanningSession state, List<PlanningDiagnostic> findings)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new TaskPlanSymbols(state.Plan!);
        foreach (var finding in findings.Where(d => d.Required && d.Code != "REVISION_REQUIRED"))
        {
            var path = finding.Location;
            if (finding.Code == "TASK_REFERENCE_UNKNOWN" && symbols.Values.TryGetValue(path, out var site) &&
                TaskPlanCompiler.Values(site.Value).Any(v => v.Kind is "output" or "present" && (v.Source is null || !symbols.Tasks.ContainsKey(v.Source))))
                return null; // A value slot cannot insert the absent business work.
            if (finding.Code == "TASK_TRANSFORM_EACH" && path.Split('/') is ["", "tasks", var task, "each"])
            {
                paths.Add(path); paths.Add("/tasks/" + task + "/inputs"); paths.Add("/tasks/" + task + "/resultType");
            }
            else paths.Add(path);
        }
        var result = paths.Where(p => !paths.Any(parent => p.StartsWith(parent + "/", StringComparison.Ordinal))).Order(StringComparer.Ordinal).ToList();
        try { TaskPlanRevisions.ValidateEditablePaths(state, result); return result; }
        catch (Exception ex) when (ex is ArgumentException or PlanningConflictException) { return null; }
    }

    private static TaskPlan ClonePlan(TaskPlan plan) => JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
}
