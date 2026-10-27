using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    // This prospective proof lives inside the existing, hashed review basis. It
    // permits only removable independent additions, preserving every byte of the
    // original plan, including transitive inputs, groups, control and effect order.
    // It neither infers resource independence nor turns an old review into a new one.
    internal static JsonObject? ContinuationDependencyProof(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        var proposed = candidate.Extension.ContinuationBinding;
        var retained = UsesIncrementalReview(state) && proposed is not null
            ? State(state).SuspendedSteps?.SingleOrDefault(item => item.Id == proposed.ContinuationId && Active(item))
            : State(state).SuspendedSteps?.LastOrDefault(Active);
        if (!UsesVerifiedProgress(state) || candidate.Extension.ContinuationBinding is not { } association ||
            retained is not { Status: "waiting" } item || item.Id != association.ContinuationId)
            return null;
        var result = new JsonObject
        {
            ["version"] = 1, ["planHash"] = candidate.Hash, ["consumerHash"] = ContinuationConsumerHash(item),
            ["authorityHash"] = ContinuationAuthorityHash(state, accepting: true),
            ["tasks"] = ContinuationStrings(TaskPlanRevisions.Tasks(candidate.Plan).Select(task => task.Id)),
            ["groups"] = ContinuationStrings(candidate.Plan.Groups.Select(group => group.Id)),
            ["openScopes"] = ContinuationStrings(candidate.OpenScopes)
        };
        if (UsesIncrementalReview(state))
        {
            var leaf = association.Binding;
            while (leaf.Kind == "field" && leaf.Items.Count == 1) leaf = leaf.Items[0];
            result["producerProof"] = leaf.Source is null ? null : PlanningConstructionDependencies.Capture(state, candidate.Plan,
                new JsonObject { ["kind"] = "task", ["id"] = leaf.Source }, candidate.OpenScopes);
            if (result["producerProof"] is null && association.DischargedContinuationIds is { Count: > 0 })
                result["exactProducerProof"] = PlanningConstructionDependencies.CaptureExactTransition(state, candidate.Plan, candidate.OpenScopes);
            result["discharged"] = ContinuationDischargeProof(state, association);
        }
        if (ResumedConsumerParent(state) is { } parent && parent.Id == association.ContinuationId)
        {
            var child = State(state).SuspendedSteps!.Single(item => item.Id == State(state).ResumingStepId);
            result["parentHandoff"] = new JsonObject
            {
                ["child"] = child.Id, ["parent"] = parent.Id, ["step"] = State(state).CurrentStep!.Id,
                ["childClaim"] = ContinuationConsumerHash(child)
            };
        }
        return result;
    }

    internal static bool ContinuationParentHandoffCurrent(PlanningSession state, PlanningConstructionStep step, JsonNode? proof)
    {
        if (!UsesAcceptedTransitions(state) || proof?["parentHandoff"] is null) return true;
        if (proof["parentHandoff"] is not JsonObject handoff || step.ContinuationBinding is not { } association ||
            association.DischargedContinuationIds is { Count: > 0 } || handoff["parent"]?.ToString() != association.ContinuationId ||
            handoff["step"]?.ToString() != step.Id || State(state).SuspendedSteps?.SingleOrDefault(item =>
                item.Id == handoff["child"]?.ToString()) is not { } child || child.ParentId != association.ContinuationId ||
            child.Status is not ("resuming" or "completed") || handoff["childClaim"]?.ToString() != ContinuationConsumerHash(child)) return false;
        return child.Status == "completed" || State(state).ResumingStepId == child.Id && State(state).CurrentStep?.Id == step.Id;
    }

    internal static string ContinuationConsumerHash(PlanningConstructionSuspendedStep item)
    {
        var value = PlanningConstruction.Node(item, PlanningJsonContext.Default.PlanningConstructionSuspendedStep)!.AsObject();
        value.Remove("status"); value.Remove("lastPrerequisiteCheckpoint");
        return PlanningGraphCompiler.Fingerprint(value.ToJsonString());
    }

    private static string ContinuationAuthorityHash(PlanningSession state, bool accepting = false) => PlanningGraphCompiler.Fingerprint(new JsonObject
    {
        ["tenant"] = state.Request.TenantId, ["session"] = state.Request.SessionId,
        ["requirements"] = PlanningConstruction.Node(accepting ? CandidateRequirements(state) : state.Requirements, PlanningJsonContext.Default.PlanningRequirements),
        ["policy"] = PlanningConstruction.Node(state.Request.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["catalogPolicy"] = PlanningConstruction.Node(state.Catalog!.Policy, PlanningJsonContext.Default.PlanningPolicy),
        ["allowedSteps"] = ContinuationStrings(state.Catalog.AllowedStepTypes), ["stepContracts"] = state.Catalog.StepContracts.DeepClone()
    }.ToJsonString());

    private static JsonArray ContinuationStrings(IEnumerable<string> values) => new(values.Order(StringComparer.Ordinal)
        .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static bool IndependentContinuationAdditions(PlanningSession state, PlanningConstructionCheckpoint checkpoint,
        PlanningConstructionSuspendedStep item)
    {
        var manifest = PlanningConstructionReviewCoverage.ReviewedContinuationBasis(state, checkpoint);
        if (manifest?["basis"]?["continuationProof"] is not JsonObject proof || proof["version"]?.ToString() != "1" ||
            proof["planHash"]?.ToString() != checkpoint.PlanHash || proof["consumerHash"]?.ToString() != ContinuationConsumerHash(item) ||
            proof["authorityHash"]?.ToString() != ContinuationAuthorityHash(state) ||
            !Identities(proof["tasks"], out var originalTasks) || !Identities(proof["groups"], out var originalGroups) ||
            !Identities(proof["openScopes"], out var originalOpenScopes) || state.Plan is null ||
            checkpoint.InvalidatedTaskIds.Count != 0 || checkpoint.Sequence < 1 || checkpoint.Sequence > state.Construction!.Checkpoints.Count ||
            !ReferenceEquals(state.Construction.Checkpoints[checkpoint.Sequence - 1], checkpoint)) return false;

        var symbols = new TaskPlanSymbols(state.Plan);
        if (symbols.InvalidIds.Count != 0 || originalTasks.Any(id => !symbols.Tasks.ContainsKey(id)) ||
            originalGroups.Any(id => state.Plan.Groups.Count(group => group.Id == id) != 1)) return false;
        var addedGroups = state.Plan.Groups.Where(group => !originalGroups.Contains(group.Id)).Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        // No call, including one in another unreferenced group, may activate a new
        // group. General effect/resource independence is deliberately not inferred.
        if (symbols.Tasks.Values.Any(site => site.Task.Kind == "call" && site.Task.Group is { } group && addedGroups.Contains(group))) return false;
        bool AddedGroupScope(string path)
        {
            var scope = symbols.Scopes.SingleOrDefault(scope => scope.Path == path);
            while (scope?.Parent is not null) scope = scope.Parent;
            return scope is not null && addedGroups.Any(id => scope.Path == "/groups/" + id + "/body");
        }
        var addedTasks = symbols.Tasks.Keys.Where(id => !originalTasks.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var rootAdditions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in addedTasks)
        {
            var site = symbols.Tasks[id];
            if (AddedGroupScope(site.Scope.Path)) continue;
            if (site.Scope.Path != "/root" || symbols.Phase(id) != "tasks" || !IndependentLiteral(site.Task)) return false;
            rootAdditions.Add(id);
        }
        var originalRootCount = state.Plan.Root.Tasks.Count(task => originalTasks.Contains(task.Id));
        if (state.Plan.Root.Tasks.Take(originalRootCount).Any(task => rootAdditions.Contains(task.Id))) return false;
        if (!state.Construction.OpenScopes.Where(path => !AddedGroupScope(path)).ToHashSet(StringComparer.Ordinal).SetEquals(originalOpenScopes)) return false;

        // Every removed addition must belong to an accepted, currently applicable
        // review in one uninterrupted checkpoint chain; orphan or replayed metadata
        // is not a dependency proof. Closure claims remain separate decisions.
        var taskOwners = new HashSet<string>(StringComparer.Ordinal); var groupOwners = new HashSet<string>(StringComparer.Ordinal);
        var previous = checkpoint.PlanHash;
        foreach (var later in state.Construction.Checkpoints.Skip(checkpoint.Sequence))
        {
            if (later.Sequence != state.Construction.Checkpoints.IndexOf(later) + 1 || later.BaseHash != previous ||
                later.InvalidatedTaskIds.Count != 0 || later.ClosedScopes.Count != 0 || later.ClosedRequirementIds is { Count: > 0 } ||
                later.Fragment["complete"]?.GetValue<bool>() == true || later.Fragment["outputs"] is not JsonArray { Count: 0 } ||
                !PlanningConstructionReviewCoverage.Reviewed(state, later, PlanningConstruction.LocalPurpose) ||
                later.NeedsIntermediateReview && !PlanningConstructionReviewCoverage.Reviewed(state, later, PlanningConstruction.IntermediatePurpose)) return false;
            foreach (var id in later.TaskIds)
                if (!addedTasks.Contains(id) || !taskOwners.Add(id) || later.Fragment["tasks"] is not JsonArray snapshots ||
                    snapshots.Count(task => task?["id"]?.ToString() == id && JsonNode.DeepEquals(task,
                        PlanningConstruction.Node(symbols.Tasks[id].Task, PlanningJsonContext.Default.PlanTask))) != 1) return false;
            if (later.Fragment["groups"] is { } groups)
            {
                if (!Identities(groups, out var ids)) return false;
                foreach (var id in ids) if (!addedGroups.Contains(id) || !groupOwners.Add(id)) return false;
            }
            previous = later.PlanHash;
        }
        if (previous != PlanningConstruction.Hash(state.Plan) || !taskOwners.SetEquals(addedTasks) || !groupOwners.SetEquals(addedGroups)) return false;
        var normalized = PlanningConstruction.Clone(state.Plan);
        normalized.Root.Tasks.RemoveAll(task => rootAdditions.Contains(task.Id));
        normalized.Groups.RemoveAll(group => addedGroups.Contains(group.Id));
        return PlanningConstruction.Hash(normalized) == checkpoint.PlanHash;

        static bool Identities(JsonNode? node, out HashSet<string> values)
        {
            values = new(StringComparer.Ordinal);
            if (node is not JsonArray array) return false;
            foreach (var entry in array)
                if (entry is not JsonValue value || !value.TryGetValue<string>(out var id) || !values.Add(id)) return false;
            return true;
        }
    }

    private static bool CurrentContinuationAuthority(PlanningSession state, PlanningConstructionCheckpoint checkpoint,
        PlanningConstructionSuspendedStep item)
    {
        var manifest = PlanningConstructionReviewCoverage.ReviewedContinuationBasis(state, checkpoint);
        if (manifest is null || checkpoint.InvalidatedTaskIds.Count != 0) return false;
        // A record without the prospective proof keeps the original whole-plan
        // condition; it never receives independent-addition eligibility retroactively.
        return manifest["basis"]?["continuationProof"] is not JsonObject proof ||
            proof["version"]?.ToString() == "1" && proof["planHash"]?.ToString() == checkpoint.PlanHash &&
            proof["consumerHash"]?.ToString() == ContinuationConsumerHash(item) &&
            proof["authorityHash"]?.ToString() == ContinuationAuthorityHash(state) &&
            (!UsesIncrementalReview(state) || JsonNode.DeepEquals(proof["discharged"], ContinuationDischargeProof(state, checkpoint.Step!.ContinuationBinding!))) &&
            ContinuationParentHandoffCurrent(state, checkpoint.Step!, proof);
    }

    private static bool IndependentLiteral(PlanTask task) => task.Kind == "value" && task.Inputs.Count == 0 && task.DependsOn.Count == 0 &&
        task.Requires is null && task.Condition is null && task.Items is null && task.Body is null && task.Otherwise is null && task.Branches.Count == 0 &&
        task.Group is null && task.Operation is null && task.Mode is null && task.Each is null && task.ResultType is null && !task.Parallel &&
        task.Outputs.Count > 0 && task.Outputs.SelectMany(output => TaskPlanCompiler.Values(output.Value))
            .All(value => value.Kind is "null" or "string" or "number" or "boolean" or "array" or "object");

    private static void RecordReviewedContinuation(PlanningSession state, PlanningConstructionCheckpoint checkpoint, string baseline)
    {
        var compact = State(state); var step = compact.CurrentStep!;
        var manifest = PlanningConstructionReviewCoverage.ReviewedContinuationBasis(state, checkpoint);
        var fingerprints = checkpoint.Reviews.Where(review => review["sourceCoverage"]?["basisHash"]?.ToString() == manifest?["basisHash"]?.ToString() &&
                review["fingerprint"] is JsonValue && review["outcome"]?.ToString() == "unverified")
            .Select(review => review["fingerprint"]!.ToString()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        checkpoint.Reviews.Add(new JsonObject
        {
            ["purpose"] = "construction_transition", ["transition"] = "resume_reviewed_consumer", ["version"] = 1,
            ["reason"] = checkpoint.PlanHash == baseline ? "reviewed_current_plan" : "reviewed_independent_additions",
            ["currentBaseline"] = baseline, ["checkpoint"] = checkpoint.Sequence, ["originCandidateHash"] = checkpoint.PlanHash,
            ["continuationId"] = compact.ResumingStepId, ["consumerStep"] = step.Id,
            ["consumerHash"] = PlanningGraphCompiler.Fingerprint(PlanningConstruction.Node(step, PlanningJsonContext.Default.PlanningConstructionStep)!.ToJsonString()),
            ["binding"] = PlanningConstruction.Node(checkpoint.Step!.ContinuationBinding!.Binding, PlanningJsonContext.Default.TaskValue),
            ["originBasisHash"] = manifest?["basisHash"]?.DeepClone(), ["reviewFingerprintCount"] = fingerprints.Length,
            ["reviewFingerprints"] = ContinuationStrings(fingerprints.Take(8)),
            ["reviewFingerprintSetHash"] = PlanningGraphCompiler.Fingerprint(ContinuationStrings(fingerprints).ToJsonString())
        });
        if (UsesIncrementalReview(state)) checkpoint.Reviews[^1]["discharged"] = ContinuationDischargeProof(state, checkpoint.Step.ContinuationBinding);
    }

    internal static JsonObject? ReviewedContinuationProvenance(PlanningSession state)
    {
        if (!UsesVerifiedProgress(state) || State(state).CurrentStep is not { } step || State(state).ResumingStepId is not { } id) return null;
        var baseline = PlanningConstruction.Hash(state.Plan);
        var consumer = PlanningGraphCompiler.Fingerprint(PlanningConstruction.Node(step, PlanningJsonContext.Default.PlanningConstructionStep)!.ToJsonString());
        foreach (var checkpoint in state.Construction!.Checkpoints.AsEnumerable().Reverse())
        {
            var row = checkpoint.Reviews.LastOrDefault(row => row["purpose"]?.ToString() == "construction_transition" &&
                row["transition"]?.ToString() == "resume_reviewed_consumer" && row["continuationId"]?.ToString() == id &&
                row["consumerStep"]?.ToString() == step.Id && row["currentBaseline"]?.ToString() == baseline && row["consumerHash"]?.ToString() == consumer);
            if (row is null || checkpoint.InvalidatedTaskIds.Count != 0) continue;
            var manifest = PlanningConstructionReviewCoverage.ReviewedContinuationBasis(state, checkpoint);
            if (manifest is null || row["originBasisHash"]?.ToString() != manifest["basisHash"]?.ToString() ||
                manifest["basis"]?["continuationProof"] is JsonObject proof && proof["authorityHash"]?.ToString() != ContinuationAuthorityHash(state)) continue;
            return row.DeepClone().AsObject();
        }
        return null;
    }
}
