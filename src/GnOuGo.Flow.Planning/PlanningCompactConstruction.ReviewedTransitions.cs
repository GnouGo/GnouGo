using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static string[] BoundaryTargets(PlanningSession state, string path)
    {
        var result = new List<string>();
        for (var scope = new TaskPlanSymbols(state.Plan ?? new()).Scopes.SingleOrDefault(scope => scope.Path == path);
             scope is not null; scope = scope.Parent)
            if (state.Construction!.OpenScopes.Contains(scope.Path, StringComparer.Ordinal)) result.Add(scope.Path);
        return result.ToArray();
    }

    private static PlanningConstructionSuspendedStep[] EligibleContinuationAssociations(PlanningSession state)
    {
        if (ResumedConsumerParent(state) is { } parent) return [parent];
        var active = State(state).SuspendedSteps?.Where(Active).ToArray() ?? [];
        if (active.Length == 0 || active.Length > 4 || active.Any(item => item.Status != "waiting" || !ContinuationCurrent(state, item))) return [];
        for (var i = 1; i < active.Length; i++)
            if (active[i].ParentId != active[i - 1].Id) return [active[^1]];
        return active;
    }

    // A resumed business child may produce its waiting parent's exact input.
    // Its ordinary acceptance completes the child; no business consumer is
    // discharged, reordered or completed merely by proposing this association.
    private static PlanningConstructionSuspendedStep? ResumedConsumerParent(PlanningSession state)
    {
        var compact = State(state);
        var active = compact.SuspendedSteps?.Where(Active).ToArray() ?? [];
        if (active.Length is < 2 or > 4 || compact.CurrentStep is not { } step ||
            active[^1] is not { Status: "resuming", ParentId: not null } child || compact.ResumingStepId != child.Id ||
            active.Take(active.Length - 1).Any(item => item.Status != "waiting") || active.Any(item => !ContinuationCurrent(state, item)) ||
            step.Scope != child.Consumer.Scope || step.Objective != child.Consumer.Objective ||
            !step.RequirementIds.SequenceEqual(child.Consumer.RequirementIds, StringComparer.Ordinal)) return null;
        for (var index = 1; index < active.Length; index++)
            if (active[index].ParentId != active[index - 1].Id) return null;
        return active[^2];
    }

    private static bool ValidContinuationDischarge(PlanningSession state, PlanningConstructionContinuationBinding association)
    {
        if (ResumedConsumerParent(state) is { } parent)
            return parent.Id == association.ContinuationId && association.DischargedContinuationIds is not { Count: > 0 };
        var chain = EligibleContinuationAssociations(state);
        var index = Array.FindIndex(chain, item => item.Id == association.ContinuationId);
        if (index < 0) return false;
        var descendants = chain.Skip(index + 1).ToArray();
        var ids = association.DischargedContinuationIds ?? [];
        if (!ids.SequenceEqual(descendants.Select(item => item.Id), StringComparer.Ordinal)) return false;
        var ancestor = chain[index];
        // Bypassing navigation cannot remove a different original obligation or
        // abandon accepted work. Parent links and retained records are unchanged.
        return descendants.All(item => item.Consumer.Scope == ancestor.Consumer.Scope &&
            item.Consumer.RequirementIds.ToHashSet(StringComparer.Ordinal).SetEquals(ancestor.Consumer.RequirementIds) &&
            state.Construction!.Checkpoints.All(checkpoint => checkpoint.Step?.Id != item.Consumer.Id));
    }

    private static void ValidateBoundaryTransition(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        var step = State(state).CurrentStep!;
        step.BoundaryTransitions = null;
        if (candidate.Extension.BoundaryTransitions is not { } claims) return;
        if (claims.Count > 4 || claims.Select(claim => claim.Scope).Distinct(StringComparer.Ordinal).Count() != claims.Count)
            PlanningConstruction.Fail("CONSTRUCTION_TRANSITION_INVALID", "/extension/boundaryTransitions", "At most four exact existing boundaries may be claimed, once each; their parent relationships determine order.");
        foreach (var claim in claims)
        if (candidate.AuthorizedPaths is not null || candidate.Extension.Complete || claim.RequirementIds.Count == 0 ||
            claim.RequirementIds.Count > step.RequirementIds.Count || claim.RequirementIds.Distinct(StringComparer.Ordinal).Count() != claim.RequirementIds.Count ||
            claim.RequirementIds.Any(id => !step.RequirementIds.Contains(id, StringComparer.Ordinal)) ||
            !BoundaryTargets(state, step.Scope).Contains(claim.Scope, StringComparer.Ordinal) ||
            claim.Outputs.Count > state.Construction!.Limits.MaxTasksPerDraft || !claim.CloseScope && claim.Outputs.Count == 0 ||
            claim.Scope == "/root" && (claim.CloseScope || claim.Outputs.Any(output => state.Requirements!.Outputs?.All(item => item.Name != output.Name) != false)) ||
            !candidate.OpenScopes.Contains(claim.Scope, StringComparer.Ordinal))
            PlanningConstruction.Fail("CONSTRUCTION_TRANSITION_INVALID", "/extension/boundaryTransitions", "Propose exact existing boundary actions for this fragment's original obligations; root exports must name declared public outputs, and root or requirement completion remain separate claims.");
        var probe = CloneTransitionState(state);
        probe.Plan = PlanningConstruction.Clone(candidate.Plan);
        probe.Construction!.OpenScopes = candidate.OpenScopes.ToList();
        probe.Construction!.Candidate = null;
        foreach (var claim in OrderedBoundaryClaims(candidate.Plan, claims))
        {
            probe.Construction.Compact!.CurrentStep = BoundaryStep(probe, claim);
            var projected = PlanningConstruction.Apply(probe, BoundaryExtension(claim));
            var validation = TaskPlanCompiler.ValidateConstruction(projected.Plan, state.Catalog!, state.Request,
                projected.OpenScopes.ToHashSet(StringComparer.Ordinal));
            if (validation.Diagnostics.Count != 0) throw new PlanningResponseException(validation.Diagnostics.ToList());
            probe.Plan = projected.Plan; probe.Construction.OpenScopes = projected.OpenScopes;
        }
        step.BoundaryTransitions = JsonSerializer.Deserialize(JsonSerializer.Serialize(claims,
            PlanningJsonContext.Default.ListPlanningConstructionBoundaryTransition), PlanningJsonContext.Default.ListPlanningConstructionBoundaryTransition);
    }

    private static IEnumerable<PlanningConstructionBoundaryTransition> OrderedBoundaryClaims(TaskPlan plan,
        IEnumerable<PlanningConstructionBoundaryTransition> claims)
    {
        var symbols = new TaskPlanSymbols(plan);
        return claims.OrderByDescending(claim => symbols.Scopes.SingleOrDefault(scope => scope.Path == claim.Scope)?.Depth ?? 0)
            .ThenBy(claim => claim.Scope, StringComparer.Ordinal);
    }

    private static PlanningConstructionStep BoundaryStep(PlanningSession state, PlanningConstructionBoundaryTransition claim) => new()
    {
        Id = "step_" + (State(state).NextStep + 1), BaseHash = PlanningConstruction.Hash(state.Plan), Scope = claim.Scope,
        Kind = "close", DiscoveryComplete = true, RequirementIds = claim.RequirementIds.ToList(),
        CloseScopes = claim.CloseScope ? [claim.Scope] : [],
        OutputNames = claim.Scope == "/root" ? claim.Outputs.Select(output => output.Name).ToList() : [],
        Objective = "Apply the exact reviewed local boundary exports and closure; preserve original obligations and accepted work."
    };

    private static PlanningConstructionExtension BoundaryExtension(PlanningConstructionBoundaryTransition claim) => new()
    {
        Scope = claim.Scope, RequirementIds = claim.RequirementIds.ToList(), Outputs = claim.Outputs.Select(output =>
            new TaskOutput(output.Name, JsonSerializer.Deserialize(JsonSerializer.Serialize(output.Value, PlanningJsonContext.Default.TaskValue),
                PlanningJsonContext.Default.TaskValue)!)).ToList(), CloseScopes = claim.CloseScope ? [claim.Scope] : []
    };

    private static PlanningSession CloneTransitionState(PlanningSession state) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;

    private static PlanningSession? PrepareReviewedBoundaryTransition(PlanningSession state, PlanningConstructionCheckpoint checkpoint)
    {
        if (state.PendingCall is not null || state.Construction!.Candidate is not null ||
            State(state).CurrentStep is not null || State(state).ReplacementScope is not null || state.EditablePaths is not null ||
            state.Diagnostics.Any(diagnostic => diagnostic.Required) ||
            state.Plan is null || state.Plan.Choices.Any(choice => choice.Selected is null) ||
            state.Construction.RebuildTasks.Any(task => !new TaskPlanSymbols(state.Plan).Tasks.ContainsKey(task.Id))) return null;
        var baseline = PlanningConstruction.Hash(state.Plan);
        if (checkpoint.Step?.BoundaryTransitions is not { Count: > 0 } claims ||
            checkpoint.InvalidatedTaskIds.Count != 0 || HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint) is not { } review ||
            !BoundaryBaselineCurrent(state, checkpoint, review)) return null;
        foreach (var claim in OrderedBoundaryClaims(state.Plan, claims))
        {
            if (!state.Construction.OpenScopes.Contains(claim.Scope, StringComparer.Ordinal)) continue;
            var identity = "transition:" + PlanningGraphCompiler.Fingerprint(new JsonObject
            {
                ["checkpoint"] = checkpoint.Sequence, ["review"] = review["requestHash"]?.DeepClone(),
                ["claim"] = PlanningConstruction.Node(claim, PlanningJsonContext.Default.PlanningConstructionBoundaryTransition)
            }.ToJsonString());
            if (State(state).FailedActions.Contains(identity, StringComparer.Ordinal)) continue;
            var probe = CloneTransitionState(state);
            try
            {
                var compact = State(probe);
                compact.CurrentStep = BoundaryStep(probe, claim); compact.NextStep++;
                var candidate = PlanningConstruction.Apply(probe, BoundaryExtension(claim));
                if (!BoundaryPreservesConsumers(state, candidate, claim)) continue;
                if (TaskPlanCompiler.ValidateConstruction(candidate.Plan, state.Catalog!, state.Request,
                    candidate.OpenScopes.ToHashSet(StringComparer.Ordinal)).Diagnostics.Count != 0) continue;
                candidate.Reviews.Add(new JsonObject
                {
                    ["purpose"] = "construction_transition", ["transition"] = "reviewed_boundary", ["version"] = 1,
                    ["identity"] = identity, ["checkpoint"] = checkpoint.Sequence, ["currentBaseline"] = baseline,
                    ["reviewRequestId"] = review["requestId"]?.DeepClone(),
                    ["reviewRequestHash"] = review["requestHash"]?.DeepClone(),
                    ["reviewResponseHash"] = review["responseHash"]?.DeepClone(),
                    ["claim"] = PlanningConstruction.Node(claim, PlanningJsonContext.Default.PlanningConstructionBoundaryTransition)
                });
                probe.Construction!.Candidate = candidate;
                compact.FailedActions.Add(identity);
                return probe;
            }
            catch (PlanningResponseException) { /* Another exact claim can remain eligible. */ }
        }
        return null;
    }

    internal static bool ReviewedBoundaryAuthority(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (candidate.Extension.Complete || candidate.AuthorizedPaths is not null ||
            candidate.BaseHash != PlanningConstruction.Hash(state.Plan) ||
            candidate.Reviews.LastOrDefault(row => row["purpose"]?.ToString() == "construction_transition" &&
                row["transition"]?.ToString() == "reviewed_boundary") is not { } transition ||
            transition["checkpoint"]?.GetValue<int>() is not { } sequence || sequence < 1 || sequence > state.Construction!.Checkpoints.Count)
            return false;
        var origin = state.Construction.Checkpoints[sequence - 1];
        if (HybridWorkflowPlanner.ReviewedCheckpoint(state, origin) is not { } review ||
            transition["currentBaseline"]?.ToString() != candidate.BaseHash ||
            transition["reviewRequestHash"]?.ToString() != review["requestHash"]?.ToString() ||
            transition["reviewResponseHash"]?.ToString() != review["responseHash"]?.ToString() ||
            !BoundaryBaselineCurrent(state, origin, review)) return false;
        var claim = origin.Step?.BoundaryTransitions?.SingleOrDefault(claim => JsonNode.DeepEquals(
            transition["claim"], PlanningConstruction.Node(claim, PlanningJsonContext.Default.PlanningConstructionBoundaryTransition)));
        return claim is not null && JsonNode.DeepEquals(
            PlanningConstruction.Node(candidate.Extension, PlanningJsonContext.Default.PlanningConstructionExtension),
            PlanningConstruction.Node(BoundaryExtension(claim), PlanningJsonContext.Default.PlanningConstructionExtension)) &&
            BoundaryPreservesConsumers(state, candidate, claim);
    }

    // A sequence of already authorized boundary applications may advance the
    // baseline. New business work or a revision needs its own current authority.
    private static bool BoundaryBaselineCurrent(PlanningSession state, PlanningConstructionCheckpoint origin, JsonObject review)
    {
        var previous = origin.PlanHash;
        foreach (var later in state.Construction!.Checkpoints.Skip(origin.Sequence))
        {
            if (later.BaseHash != previous || later.InvalidatedTaskIds.Count != 0 ||
                !later.Reviews.Any(row => row["purpose"]?.ToString() == "construction_transition" &&
                    row["transition"]?.ToString() == "reviewed_boundary" && row["checkpoint"]?.ToString() == origin.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                    row["reviewRequestHash"]?.ToString() == review["requestHash"]?.ToString() &&
                    row["reviewResponseHash"]?.ToString() == review["responseHash"]?.ToString())) return false;
            previous = later.PlanHash;
        }
        return previous == PlanningConstruction.Hash(state.Plan);
    }

    private static bool BoundaryPreservesConsumers(PlanningSession state, PlanningConstructionCandidate candidate,
        PlanningConstructionBoundaryTransition claim)
    {
        foreach (var item in State(state).SuspendedSteps?.Where(Active) ?? [])
        {
            if (item.Status != "waiting" || !ContinuationCurrent(state, item) ||
                claim.CloseScope && ScopeWithin(state.Plan!, item.Consumer.Scope, claim.Scope) ||
                claim.Scope == "/root" && claim.Outputs.Any(output => item.Consumer.OutputNames.Contains(output.Name, StringComparer.Ordinal))) return false;
            var before = CloneTransitionState(state);
            var after = CloneTransitionState(state);
            after.Plan = candidate.Plan; after.Construction!.OpenScopes = candidate.OpenScopes.ToList();
            if (!ContinuationCurrent(after, item)) return false;
            // Preserve actual typed imports, including captures and isolated group
            // parameters. A matching type or lack of edges is not independence.
            try
            {
                var evidence = State(state).RejectedSteps[item.RejectedStepIndex]["evidence"] as JsonObject;
                before.Construction!.Evidence = evidence?.DeepClone().AsObject();
                after.Construction.Evidence = evidence?.DeepClone().AsObject();
                var original = CloneStep(item.Consumer); var projected = CloneStep(item.Consumer);
                ValidateSelection(before, original); ValidateSelection(after, projected);
                if (!JsonNode.DeepEquals(ImportedInterfaces(before, original), ImportedInterfaces(after, projected))) return false;
            }
            catch (PlanningResponseException) { return false; }
        }
        return true;
    }

    private static bool TryResumeReviewedAncestor(PlanningSession state)
    {
        var checkpoint = state.Construction!.Checkpoints.LastOrDefault();
        if (checkpoint?.Step?.ContinuationBinding is not { DischargedContinuationIds.Count: > 0 } association ||
            !ValidContinuationDischarge(state, association) || checkpoint.PlanHash != PlanningConstruction.Hash(state.Plan) ||
            HybridWorkflowPlanner.ReviewedCheckpoint(state, checkpoint) is null) return false;
        var original = State(state).SuspendedSteps!.Single(item => item.Id == association.ContinuationId);
        if (!CurrentContinuationAuthority(state, checkpoint, original)) return false;
        var probe = CloneTransitionState(state);
        foreach (var id in association.DischargedContinuationIds)
            State(probe).SuspendedSteps!.Single(item => item.Id == id).Status = "prerequisite_discharged";
        try { ResumeStep(probe, association.ContinuationId, checkpoint.Sequence, association.Binding); }
        catch (PlanningResponseException) { return false; }
        RecordReviewedContinuation(probe, probe.Construction!.Checkpoints[checkpoint.Sequence - 1], checkpoint.PlanHash);
        state.Construction = probe.Construction; state.Diagnostics = probe.Diagnostics;
        return true;
    }
}
