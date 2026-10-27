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
        if (!UsesAcceptedTransitions(state)) return null;
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

    private static JsonArray ContinuationDischargeProof(PlanningSession state, PlanningConstructionContinuationBinding association) =>
        new((association.DischargedContinuationIds ?? []).Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id,
            ["retainedHash"] = State(state).SuspendedSteps?.SingleOrDefault(item => item.Id == id) is { } item ? ContinuationConsumerHash(item) : null
        }).ToArray());

    private static void ValidateBoundaryTransition(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (!UsesIncrementalReview(state)) return;
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
        probe.Construction.Candidate = null;
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
        int Depth(string path)
        {
            var depth = 0;
            for (var scope = symbols.Scopes.SingleOrDefault(scope => scope.Path == path); scope?.Parent is not null; scope = scope.Parent) depth++;
            return depth;
        }
        return claims.OrderByDescending(claim => Depth(claim.Scope)).ThenBy(claim => claim.Scope, StringComparer.Ordinal);
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

    internal static JsonObject? BoundaryDependencyProof(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (!UsesIncrementalReview(state) || candidate.Extension.BoundaryTransitions is not { Count: > 0 } claims) return null;
        var probe = CloneTransitionState(state);
        probe.Plan = PlanningConstruction.Clone(candidate.Plan); probe.Construction!.OpenScopes = candidate.OpenScopes.ToList();
        probe.Construction.Candidate = null;
        var entries = new JsonArray();
        foreach (var claim in OrderedBoundaryClaims(candidate.Plan, claims))
        {
            State(probe).CurrentStep = BoundaryStep(probe, claim);
            PlanningConstructionCandidate projected;
            try { projected = PlanningConstruction.Apply(probe, BoundaryExtension(claim)); }
            catch (PlanningResponseException) { return null; }
            var proofs = new JsonArray();
            if (claim.CloseScope)
            {
                var proof = PlanningConstructionDependencies.Capture(probe, projected.Plan,
                    new JsonObject { ["kind"] = "scope", ["path"] = claim.Scope }, projected.OpenScopes);
                if (proof is not null) proofs.Add((JsonNode)proof);
            }
            else
            {
                var sources = claim.Outputs.SelectMany(output => TaskPlanCompiler.Values(output.Value))
                    .Where(value => value.Kind is "output" or "present").Select(value => value.Source).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                foreach (var source in sources)
                {
                    var proof = PlanningConstructionDependencies.Capture(probe, projected.Plan,
                        new JsonObject { ["kind"] = "task", ["id"] = source }, projected.OpenScopes);
                    if (proof is null) { proofs.Clear(); break; }
                    proofs.Add((JsonNode)proof);
                }
            }
            var entry = new JsonObject { ["scope"] = claim.Scope, ["projectedHash"] = projected.Hash, ["proofs"] = proofs };
            if (proofs.Count == 0)
            {
                // Unknown behavior permits only exact prospective world equality,
                // never an inference that later work is independent.
                var exact = PlanningConstructionDependencies.CaptureExactTransition(probe, projected.Plan, projected.OpenScopes);
                if (exact is null) return null;
                entry["exactProof"] = exact;
            }
            entries.Add((JsonNode)entry);
            probe.Plan = projected.Plan; probe.Construction.OpenScopes = projected.OpenScopes;
        }
        return new JsonObject { ["boundaries"] = entries };
    }

    private static PlanningSession? PrepareReviewedBoundaryTransition(PlanningSession state, PlanningConstructionCheckpoint checkpoint)
    {
        if (!UsesIncrementalReview(state) || state.PendingCall is not null || state.Construction!.Candidate is not null ||
            State(state).CurrentStep is not null || State(state).ReplacementScope is not null || state.EditablePaths is not null ||
            state.Diagnostics.Any(diagnostic => diagnostic.Required) ||
            !UsesAcceptedTransitions(state) && State(state).SuspendedSteps?.Any(Active) == true ||
            state.Plan is null || state.Plan.Choices.Any(choice => choice.Selected is null) ||
            state.Construction.RebuildTasks.Any(task => !new TaskPlanSymbols(state.Plan).Tasks.ContainsKey(task.Id))) return null;
        var baseline = PlanningConstruction.Hash(state.Plan);
        if (checkpoint.Step?.BoundaryTransitions is not { Count: > 0 } claims ||
            checkpoint.InvalidatedTaskIds.Count != 0 || PlanningConstructionReviewCoverage.ReviewedTransitionBasis(state, checkpoint) is not { } basis ||
            basis["basis"]?["boundaryProof"]?["boundaries"] is not JsonArray entries) return null;
        foreach (var claim in OrderedBoundaryClaims(state.Plan, claims))
        {
            if (!state.Construction.OpenScopes.Contains(claim.Scope, StringComparer.Ordinal) ||
                entries.OfType<JsonObject>().SingleOrDefault(entry => entry["scope"]?.ToString() == claim.Scope) is not { } dependency) continue;
            var identity = "transition:" + PlanningGraphCompiler.Fingerprint(new JsonObject
            {
                ["checkpoint"] = checkpoint.Sequence, ["basis"] = basis["basisHash"]?.DeepClone(),
                ["claim"] = PlanningConstruction.Node(claim, PlanningJsonContext.Default.PlanningConstructionBoundaryTransition)
            }.ToJsonString());
            if (State(state).FailedActions.Contains(identity, StringComparer.Ordinal)) continue;
            var probe = CloneTransitionState(state);
            try
            {
                var compact = State(probe);
                compact.CurrentStep = BoundaryStep(probe, claim); compact.NextStep++;
                probe.Construction!.CandidateFailureIdentityVersion = 1;
                var candidate = PlanningConstruction.Apply(probe, BoundaryExtension(claim));
                if (UsesAcceptedTransitions(state) && !BoundaryPreservesConsumers(state, candidate, claim)) continue;
                if (checkpoint.PlanHash == baseline && dependency["projectedHash"]?.ToString() != candidate.Hash) continue;
                var preserved = dependency["proofs"] is JsonArray { Count: > 0 } proofs
                    ? proofs.All(proof => proof is JsonObject entry && PlanningConstructionDependencies.Preserved(probe,
                        candidate.Plan, entry, candidate.OpenScopes))
                    : dependency["exactProof"] is JsonObject exact && PlanningConstructionDependencies.PreservedExactTransition(probe,
                        candidate.Plan, exact, candidate.OpenScopes);
                if (!preserved) continue;
                candidate.NeedsIntermediateReview = true;
                candidate.Reviews.Add(new JsonObject
                {
                    ["purpose"] = "construction_transition", ["transition"] = "reviewed_boundary", ["version"] = 1,
                    ["identity"] = identity, ["checkpoint"] = checkpoint.Sequence, ["currentBaseline"] = baseline,
                    ["originBasisHash"] = basis["basisHash"]?.DeepClone(),
                    ["claim"] = PlanningConstruction.Node(claim, PlanningJsonContext.Default.PlanningConstructionBoundaryTransition)
                });
                probe.Construction.Candidate = candidate;
                compact.FailedActions.Add(identity);
                return probe;
            }
            catch (PlanningResponseException) { /* Another exact claim can remain eligible. */ }
        }
        return null;
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
            PlanningConstructionReviewCoverage.ReviewedTransitionBasis(state, checkpoint) is not { } manifest) return false;
        var original = State(state).SuspendedSteps!.Single(item => item.Id == association.ContinuationId);
        if (!CurrentContinuationAuthority(state, checkpoint, original)) return false;
        var proof = manifest["basis"]?["continuationProof"];
        var preserved = proof?["producerProof"] is JsonObject producerProof
            ? PlanningConstructionDependencies.Preserved(state, state.Plan!, producerProof)
            : proof?["exactProducerProof"] is JsonObject exact && PlanningConstructionDependencies.PreservedExactTransition(state, state.Plan!, exact);
        if (!preserved) return false;
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
