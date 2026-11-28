using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    // Construction checkpoints are technical transactions, not a Judge schedule.
    // Review a completed business boundary; prefer one global review when the
    // remaining work is only finalization. No scenario or checkpoint-count rule.
    internal static bool NeedsBusinessReview(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (candidate.Extension.Complete || candidate.AuthorizedPaths is { Count: > 0 }) return true;
        if (PlanningCompactConstruction.ReviewedBoundaryAuthority(state, candidate)) return false;
        if (!PlanningCompactConstruction.CandidateCompletesBlock(candidate)) return false;
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        // Retain the explicit claim pending mandatory global review when only
        // finalization remains. Technical acceptance does not close the block.
        if (construction.Requirements.All(requirement => PlanningCompactConstruction.RequirementCompletionClaimed(state, requirement.Id) ||
            step.CloseRequirementIds.Contains(requirement.Id, StringComparer.Ordinal))) return false;
        return true;
    }

    internal static bool CanWithdrawRejectedClaim(PlanningSession state)
    {
        var construction = state.Construction;
        if (state.EditablePaths is not null || state.Request.Options["automatic_correction"]?.ToString() == "false" ||
            construction?.Compact?.CurrentStep is not { } step ||
            construction.Candidate is not { AuthorizedPaths.Count: > 0, BusinessReviewed: false } candidate ||
            candidate.Extension is not { CompleteBlock: true, Complete: false, CloseScopes.Count: 0,
                Tasks.Count: 0, Always.Count: 0, Outputs.Count: 0, Groups.Count: 0, Choices.Count: 0,
                ContinuationBinding: null, BoundaryTransitions: null or { Count: 0 }, ContinueInScope: null } ||
            candidate.Extension.Scope != step.Scope || !construction.OpenScopes.Contains(step.Scope) ||
            candidate.BaseHash != candidate.Hash || candidate.Hash != PlanningConstruction.Hash(state.Plan) ||
            candidate.Hash != PlanningConstruction.Hash(candidate.Plan) ||
            !candidate.Diagnostics.Any(d => d.Required && d.Code == "CONSTRUCTION_BUSINESS_REVIEW" && d.ValidationStage == PlanningConstruction.LocalPurpose)) return false;

        var review = candidate.Reviews.LastOrDefault(row => row["purpose"]?.ToString() == PlanningConstruction.LocalPurpose);
        return review?["outcome"]?.ToString() == "needs_change" && review["defectWitness"] is JsonObject &&
            review["pendingRepairPaths"] is JsonArray paths && candidate.AuthorizedPaths.ToHashSet(StringComparer.Ordinal)
                .SetEquals(paths.Select(path => path!.ToString())) &&
            review["candidateHash"]?.ToString() == candidate.Hash && review["claimsHash"]?.ToString() == ConstructionClaimsHash(state) &&
            BusinessSourcesAndContractsValid(state, review);
    }

    // New scope exports, pending accepted tasks and concrete current global
    // findings retain bounded functional repair, never structural authority.
    private static void AuthorizeBusinessRepair(PlanningSession state, JsonObject review, string[] targets)
    {
        var construction = state.Construction!; var candidate = construction.Candidate!;
        if (candidate.BaseHash != PlanningConstruction.Hash(state.Plan) || state.EditablePaths is not null) return;
        var currentReview = review["candidateHash"]?.ToString() == candidate.Hash &&
            review["claimsHash"]?.ToString() == ConstructionClaimsHash(state) && BusinessSourcesAndContractsValid(state, review);
        var global = currentReview && candidate.Extension.Complete && review["purpose"]?.ToString() == PlanningConstruction.GlobalPurpose;
        var local = currentReview && !candidate.Extension.Complete && review["purpose"]?.ToString() == PlanningConstruction.LocalPurpose;
        var accepted = new TaskPlanSymbols(state.Plan ?? new()); var current = new TaskPlanSymbols(candidate.Plan);
        var paths = new List<string>(); var tasks = new List<string>();
        var readOnlyTargets = new List<string>();
        var citedScopes = new List<TaskPlanSymbols.Scope>();
        var tentativeScope = false;
        foreach (var target in targets.Where(target => !candidate.TaskIds.Contains(target, StringComparer.Ordinal)))
        {
            if (target == candidate.Extension.Scope || target.StartsWith("output:", StringComparison.Ordinal) &&
                candidate.Extension.Outputs.Any(output => "output:" + output.Name == target)) continue;
            // Check the relationship after all cited tasks have independently
            // qualified. A scope can explain an export without being editable.
            if (local && accepted.Scopes.SingleOrDefault(scope => scope.Path == target) is { Owner: not null } branch)
            {
                citedScopes.Add(branch);
                continue;
            }
            if (current.Scopes.Any(scope => scope.Path == target) && accepted.Scopes.All(scope => scope.Path != target))
            {
                var exports = current.Values.Keys.Where(path => path.StartsWith(target + "/outputs/", StringComparison.Ordinal)).ToArray();
                if (exports.Length == 0) return;
                paths.AddRange(exports); tentativeScope = true;
                continue;
            }
            if (!accepted.Tasks.TryGetValue(target, out var task) || !current.Tasks.TryGetValue(target, out var next) ||
                task.Scope.Path != next.Scope.Path || accepted.Phase(target) != current.Phase(target) ||
                !JsonNode.DeepEquals(PlanningConstruction.TaskNode(task.Task), PlanningConstruction.TaskNode(next.Task))) return;
            var owners = construction.Checkpoints.Where(checkpoint => checkpoint.TaskIds.Contains(target, StringComparer.Ordinal) &&
                !checkpoint.InvalidatedTaskIds.Contains(target, StringComparer.Ordinal)).ToArray();
            if (owners.Length == 0 || owners[^1].Fragment["tasks"] is not JsonArray original || !original.Any(node => node?["id"]?.ToString() == target &&
                    JsonNode.DeepEquals(node, PlanningConstruction.Node(task.Task, PlanningJsonContext.Default.PlanTask)))) return;
            var pending = owners[^1].Fragment["businessReviewPending"]?.GetValue<bool>() == true;
            if (!pending)
            {
                if (!ReviewedTarget(owners[^1], target)) return;
                // A local finding can cite the earlier reviewed producer as evidence
                // for a repair at its pending consumer. Only the consumer is editable.
                if (local) { readOnlyTargets.Add(target); continue; }
                if (!global) return;
            }
            var offered = TaskRepairPaths(accepted, task.Task, offerMissingGuard: pending).ToList();
            if (local && pending && task.Task.Kind == "transform")
            {
                if (task.Task.Requires is null) offered.Add("/tasks/" + target + "/requires");
                var bindings = current.Values.Values.Select(site => site.Value)
                    .Concat(candidate.Plan.Choices.SelectMany(choice => choice.Alternatives).Select(alternative => alternative.Value));
                if (!bindings.SelectMany(TaskPlanCompiler.Values).Any(value => value.Kind == "output" && value.Source == target))
                    offered.Add("/tasks/" + target + "/resultType");
            }
            if (offered.Count == 0) return;
            paths.AddRange(offered); tasks.Add(target);
        }
        foreach (var branch in citedScopes)
        {
            var exportsCitedProducer = branch.Source.Outputs.SelectMany(output => TaskPlanCompiler.Values(output.Value))
                .Any(value => value is { Kind: "output", Source: { } source } && CitedRepairableTask(source) &&
                    accepted.Tasks.TryGetValue(source, out var producer) && producer.Scope.Path == branch.Path);
            if (!(CitedRepairableTask(branch.Owner!.Id) || exportsCitedProducer) ||
                current.Scopes.SingleOrDefault(scope => scope.Path == branch.Path) is not { } proposed ||
                !JsonNode.DeepEquals(PlanningConstruction.Node(branch.Source, PlanningJsonContext.Default.TaskScope),
                    PlanningConstruction.Node(proposed.Source, PlanningJsonContext.Default.TaskScope))) return;
            readOnlyTargets.Add(branch.Path);
        }
        if (tentativeScope || local && readOnlyTargets.Count > 0)
            foreach (var target in targets.Where(target => candidate.TaskIds.Contains(target, StringComparer.Ordinal)))
            {
                if (!current.Tasks.TryGetValue(target, out var task)) return;
                if (candidate.AuthorizedPaths is { Count: > 0 } issued)
                    paths.AddRange(issued.Where(path => path.StartsWith("/tasks/" + target + "/", StringComparison.Ordinal)));
                else
                    paths.AddRange(TaskRepairPaths(current, task.Task, offerMissingGuard: local && !accepted.Tasks.ContainsKey(target)));
            }
        if (paths.Count == 0) return;
        var authorized = (candidate.AuthorizedPaths ?? []).Concat(paths).Distinct(StringComparer.Ordinal).ToList();
        try { TaskPlanRevisions.ValidateEditablePaths(new() { Plan = candidate.Plan, Catalog = state.Catalog }, authorized); }
        catch (ArgumentException) { return; }
        candidate.AuthorizedPaths = authorized;
        candidate.TaskIds = candidate.TaskIds.Concat(tasks).Distinct(StringComparer.Ordinal).ToList();
        review["pendingRepairPaths"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        if (readOnlyTargets.Count > 0)
            review["readOnlyTargets"] = new JsonArray(readOnlyTargets.Select(target => (JsonNode?)JsonValue.Create(target)).ToArray());

        bool CitedRepairableTask(string id) => tasks.Contains(id, StringComparer.Ordinal) ||
            targets.Contains(id, StringComparer.Ordinal) && candidate.TaskIds.Contains(id, StringComparer.Ordinal) &&
            candidate.AuthorizedPaths?.Any(path => path.StartsWith("/tasks/" + id + "/", StringComparison.Ordinal)) == true;

        bool ReviewedTarget(PlanningConstructionCheckpoint owner, string target)
        {
            if (ReviewedCheckpoint(state, owner) is not null) return true;
            var block = construction.Compact!.Blocks?.SingleOrDefault(block => block.Id == owner.Step?.BlockId);
            return block?.CompletionCheckpoint > owner.Sequence && PlanningCompactConstruction.BusinessBlockReview(state, block)?["context"]?["actions"] is JsonArray actions &&
                actions.Any(action => action?["id"]?.ToString() == target);
        }

        static IEnumerable<string> TaskRepairPaths(TaskPlanSymbols symbols, PlanTask task, bool offerMissingGuard = false) => task.Kind switch
        {
            "transform" => ["/tasks/" + task.Id + "/inputs", "/tasks/" + task.Id + "/objective"],
            "operation" or "call" when offerMissingGuard && task.Requires is null => ["/tasks/" + task.Id + "/inputs", "/tasks/" + task.Id + "/requires"],
            "operation" or "call" => ["/tasks/" + task.Id + "/inputs"],
            _ => symbols.Values.Keys.Where(path => path.StartsWith("/tasks/" + task.Id + "/outputs/", StringComparison.Ordinal) ||
                task.Kind == "foreach" && path == "/tasks/" + task.Id + "/items" ||
                task.Kind == "conditional" && path == "/tasks/" + task.Id + "/condition")
        };
    }

    // Unsupported targets retain evidence-only authority.
    private static async Task RetrieveBusinessFindingEvidenceAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await ReserveConstructionReviewAsync(state, runtime, ct);
        var context = PlanningBusinessProjection.Build(state, state.Construction!.Candidate!.Extension.Complete);
        var prior = state.Construction.Candidate.Reviews.Last(review => review["outcome"]?.ToString() == "needs_change");
        context["finding"] = prior["response"]!["decision"]!.DeepClone();
        var addresses = BusinessReviewReconsiderationEvidence(state);
        if (addresses.Length == 0) { Stop(state); return; }
        var schema = PlanningSchemas.Object(("patch", PlanningSchemas.Type("null")),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1, PlanningOptimizationThresholds.Maximum(state, 8)))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications(PlanningTokenThresholds.OptimizationAdvisory(state.Request)))));
        var request = state.PendingCall?.Request ?? PlanningCompactConstruction.Request(
            "Use the existing original-evidence action to examine this business finding. Accepted work is immutable here: patch is null. Request exact evidence addressing the cited obligation or implementation when the finding needs reconsideration on the unchanged facts; otherwise clarify the missing business fact. No evidence grants editing or execution authority.",
            context, schema, "replan");
        using var accounting = new PlanningCorrectionAttempt(state, correcting: false);
        var response = await PlanningModelCalls.CallAsync(state, runtime, "replan", request.Prompt,
            request.StructuredOutputSchema!.AsObject(), ct, correction: accounting);
        if (response["evidenceRequests"] is JsonArray evidence && response["clarifications"] is null &&
            TryPrepareBusinessReviewReconsideration(state, evidence.Select(address => address!.ToString()).ToArray()))
        { accounting.EvidenceAccepted(); return; }
        if (response["clarifications"] is JsonArray questions && response["evidenceRequests"] is null)
        {
            var parsed = questions.Deserialize(PlanningJsonContext.Default.ListPlanningQuestion)!;
            PlanningClarifications.Suspend(state, parsed); state.PendingQuestions = parsed; state.Status = PlanningStatus.Clarification;
            accounting.EvidenceAccepted(); return;
        }
        state.Diagnostics = state.Construction.Candidate.Diagnostics;
        Stop(state);
    }

}
