using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private static readonly string[] NestedControlKinds = ["sequence", "conditional", "foreach", "parallel"];

    internal static bool SupportsBusinessFragment(string? kind) => kind is not (null or "close" or "choice" or "revision");

    private static IEnumerable<PlanningConstructionCheckpoint> BlockCheckpoints(PlanningSession state) =>
        state.Construction!.Checkpoints.Where(checkpoint => checkpoint.Step?.Id == State(state).CurrentStep!.Id &&
            checkpoint.InvalidatedTaskIds.Count == 0);

    internal static bool BlockAnchorConstructed(PlanningSession state)
    {
        var step = State(state).CurrentStep!;
        var symbols = new TaskPlanSymbols(state.Plan ?? new());
        return BlockCheckpoints(state).Any(checkpoint => step.Kind == "group"
            ? checkpoint.Fragment["groups"] is JsonArray { Count: > 0 }
            : checkpoint.TaskIds.Any(id => symbols.Tasks.TryGetValue(id, out var task) && task.Task.Kind == step.Kind));
    }

    internal static HashSet<string> BusinessFragmentKinds(PlanningSession state) => BusinessFragmentKinds(state, out _, out _);

    private static HashSet<string> BusinessFragmentKinds(PlanningSession state, out HashSet<(string Id, string Port)> controlExports,
        out HashSet<string> sequenceGuardSources)
    {
        var step = State(state).CurrentStep!;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        controlExports = [];
        sequenceGuardSources = [];
        var anchorConstructed = BlockAnchorConstructed(state);
        if (!anchorConstructed && step.Kind is not (null or "group" or "close" or "choice")) kinds.Add(step.Kind!);
        if (SupportsBusinessFragment(step.Kind))
        {
            kinds.Add("value"); kinds.Add("transform");
            if (step.OperationIds.Count > 0) kinds.Add("operation");
            if (step.GroupId is not null) kinds.Add("call");
            var checkpoints = BlockCheckpoints(state).ToArray();
            if (checkpoints.FirstOrDefault()?.Step?.Scope is { } origin)
            {
                if (origin != step.Scope) kinds.UnionWith(NestedControlKinds);
                else if (NestedControlKinds.Contains(step.Kind, StringComparer.Ordinal))
                {
                    var symbols = new TaskPlanSymbols(state.Plan!);
                    var anchorHasExports = false;
                    foreach (var id in checkpoints.SelectMany(checkpoint => checkpoint.TaskIds))
                        if (symbols.Tasks.TryGetValue(id, out var site) && site.Scope.Path == step.Scope &&
                            NestedControlKinds.Contains(site.Task.Kind, StringComparer.Ordinal))
                        {
                            var resultScopes = symbols.Scopes.Where(scope => scope.Owner == site.Task).ToArray();
                            if (resultScopes.Any(scope => state.Construction!.OpenScopes.Contains(scope.Path, StringComparer.Ordinal))) continue;
                            foreach (var scope in resultScopes)
                                foreach (var output in scope.Source.Outputs)
                                {
                                    controlExports.Add((id, output.Name));
                                    anchorHasExports |= site.Task.Kind == step.Kind;
                                }
                        }
                    if (anchorHasExports) kinds.UnionWith(NestedControlKinds.Where(kind => kind != step.Kind));
                    else controlExports.Clear();
                    if (!anchorConstructed && step.Kind == "sequence" &&
                        ContinuedCheckpoint(state, step) is { InvalidatedTaskIds.Count: 0 } prior &&
                        symbols.Scopes.FirstOrDefault(site => site.Path == step.Scope) is { } scopeSite)
                    {
                        sequenceGuardSources.UnionWith(scopeSite.Source.Tasks.Where(task => prior.TaskIds.Contains(task.Id) &&
                            task.Kind is "operation" or "transform" or "value" or "call").Select(task => task.Id));
                        if (sequenceGuardSources.Count > 0) kinds.Add("conditional");
                    }
                }
            }
        }
        return kinds;
    }

    internal static bool CanFinishContinuedBlock(PlanningSession state, PlanningConstructionExtension extension) =>
        extension.CompleteBlock == true && extension.ContinueInScope is null &&
        BusinessBlockCheckpoints(state).Any(checkpoint => checkpoint.TaskIds.Count > 0);

    private static bool CanClaimCurrentBlockCompletion(PlanningSession state) =>
        State(state).CurrentStep?.CloseRequirementIds.Count == 0 && HasAcceptedCompletionBasis(state);

    private static bool HasAcceptedCompletionBasis(PlanningSession state) =>
        State(state).CurrentStep is { BlockId: { } block } step &&
        CurrentBlock(state) is { CompletionCheckpoint: null } current && current.Id == block &&
        step.BaseHash == PlanningConstruction.Hash(state.Plan) &&
        CanFinishContinuedBlock(state, new() { CompleteBlock = true });

    internal static List<string> CandidateCompletionClaims(PlanningSession state) =>
        CandidateCompletesBlock(state) || state.Construction!.Candidate?.AuthorizedPaths is not null
            ? State(state).CurrentStep!.CloseRequirementIds : [];

    private static void ValidateEmptyOpening(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        var extension = candidate.Extension;
        var tasks = TaskPlanRevisions.Tasks(new TaskScope { Tasks = extension.Tasks, Always = extension.Always })
            .Concat(extension.Groups.SelectMany(group => TaskPlanRevisions.Tasks(group.Body))).ToArray();
        var opens = extension.Groups.Count > 0 || tasks.Any(task => task.Kind is "sequence" or "conditional" or "parallel" or "foreach");
        if (!opens || extension.Outputs.Count > 0 || tasks.Any(task => task.Kind is not ("sequence" or "conditional" or "parallel" or "foreach")) ||
            tasks.Any(task => task.Body?.Outputs.Count > 0 || task.Otherwise?.Outputs.Count > 0 || task.Branches.Any(branch => branch.Outputs.Count > 0)) ||
            extension.Groups.Any(group => group.Body.Outputs.Count > 0)) return;
        if (HasCurrentOpeningPrerequisite(state)) return;
        PlanningConstruction.Fail("CONSTRUCTION_EMPTY_OPENING", "/extension",
            "Populate this business block's control or group body. An empty-only opening requires an existing evidence-linked unresolved prerequisite for the current responsibility.");
    }

    internal static bool HasCurrentOpeningPrerequisite(PlanningSession state)
    {
        var step = State(state).CurrentStep!;
        ValidatePrerequisites(state, step);
        return step.Prerequisites is { Count: > 0 } || (State(state).SuspendedSteps ?? []).Any(item => item.Status == "waiting" &&
            item.Consumer.RequirementIds.Any(id => step.RequirementIds.Contains(id, StringComparer.Ordinal)) && ContinuationCurrent(state, item));
    }

    private static IReadOnlyList<PlanningDiagnostic> ValidateBlockContinuation(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (candidate.Extension.ContinueInScope is not { } path) return [];
        var step = State(state).CurrentStep!;
        if (!SupportsBusinessFragment(step.Kind) || candidate.AuthorizedPaths is not null || CandidateCompletesBlock(candidate) ||
            candidate.Extension.CloseScopes.Contains("/root", StringComparer.Ordinal) ||
            candidate.Extension.ContinuationBinding is not null || candidate.Extension.BoundaryTransitions is { Count: > 0 } ||
            !candidate.OpenScopes.Contains(path, StringComparer.Ordinal))
            PlanningConstruction.Fail("CONSTRUCTION_BLOCK_CONTINUATION_INVALID", "/extension/continueInScope",
                "Continue only an unfinished business block in an exact open scope, without completion or deferred consumer/boundary claims.");
        // Rollback can reopen a surviving container after removing one of its children.
        // Its creation still establishes scope ownership; the fragment still needs review.
        var rebuildingScope = state.Construction!.RebuildScopes.Any(placement => placement.Path == path);
        var checkpoints = state.Construction.Checkpoints.Where(checkpoint =>
            checkpoint.Step?.BlockId is { } block && block == step.BlockId &&
            (checkpoint.InvalidatedTaskIds.Count == 0 || rebuildingScope)).ToArray();
        var origin = BlockCheckpoints(state).FirstOrDefault()?.Step?.Scope ?? step.Scope;
        var ownedTasks = checkpoints.SelectMany(checkpoint => checkpoint.TaskIds.Except(checkpoint.InvalidatedTaskIds, StringComparer.Ordinal))
            .Concat(candidate.TaskIds).ToHashSet(StringComparer.Ordinal);
        var ownedGroups = checkpoints.SelectMany(checkpoint => (checkpoint.Fragment["groups"] as JsonArray ?? []).Select(group => group!.ToString()))
            .Concat(candidate.Extension.Groups.Select(group => group.Id)).ToHashSet(StringComparer.Ordinal);
        var symbols = new TaskPlanSymbols(candidate.Plan);
        var scope = symbols.Scopes.SingleOrDefault(scope => scope.Path == path);
        var owned = scope is not null && (scope.Owner is { } owner ? ownedTasks.Contains(owner.Id)
            : ownedGroups.Any(group => path == "/groups/" + group + "/body"));
        if (path != origin && !owned)
            PlanningConstruction.Fail("CONSTRUCTION_BLOCK_CONTINUATION_INVALID", "/extension/continueInScope",
                "A continuation must use the original business scope or an open control/group body created by this block.");
        var probe = CloneTransitionState(state);
        probe.Plan = PlanningConstruction.Clone(candidate.Plan);
        probe.Construction!.OpenScopes = candidate.OpenScopes.ToList();
        PlanningConstruction.AdmitResolvedOperations(probe, candidate.Plan);
        var continuation = CloneStep(step);
        continuation.Scope = path;
        // Selection inputs are presentation hints. The new scope supplies its own
        // exact interfaces; the ordinary compiler still checks every actual use.
        if (path != step.Scope) { continuation.Inputs.Clear(); continuation.DependencyTaskIds.Clear(); }
        if (State(probe).ReplacementScope is not null) State(probe).ReplacementScope = path;
        ValidateSelectionStructure(probe, continuation);
        if (continuation.Inputs.Count == 0) return [];
        var validation = InspectImportedInterfaces(probe, continuation);
        // A bad consumer anywhere in the tentative fragment clears all captured
        // interfaces. Keep its findings, never publish unchecked interfaces or
        // blame a valid accepted import for that consumer's error.
        if (validation.Diagnostics.Count > 0) return validation.Diagnostics;
        _ = ImportedInterfaces(probe, continuation, validation);
        return [];
    }

    private static PlanningConstructionCheckpoint? ContinuedCheckpoint(PlanningSession state, PlanningConstructionStep step)
    {
        var checkpoint = state.Construction!.Checkpoints.LastOrDefault();
        return checkpoint?.Step is { } prior && prior.Id == step.Id && prior.BlockId == step.BlockId && prior.Scope == step.Scope &&
            checkpoint.Fragment["continueInScope"]?.ToString() == step.Scope && checkpoint.PlanHash == step.BaseHash ? checkpoint : null;
    }

    internal static bool TryEnterAcceptedChildScope(PlanningSession state, PlanningConstructionResponse proposal)
    {
        if (proposal is not { Requirements: null, Commitments: null or { Count: 0 }, RollbackReason: null,
                Extension: { Complete: false, CompleteBlock: false, ContinueInScope: { } path,
                    Tasks.Count: 0, Always.Count: 0, Groups.Count: 0, Choices.Count: 0, Outputs.Count: 0, CloseScopes.Count: 0,
                    BoundaryTransitions: null or { Count: 0 }, ContinuationBinding: null } extension } ||
            state.EditablePaths is not null || state.Plan is null ||
            state.Construction is not { Candidate: null, Compact: { ReplacementScope: null, CurrentStep: { } step } } construction ||
            !SupportsBusinessFragment(step.Kind) || extension.Scope != step.Scope || path == step.Scope ||
            !construction.OpenScopes.Contains(path, StringComparer.Ordinal) ||
            extension.RequirementIds.Count != step.RequirementIds.Count || !step.RequirementIds.ToHashSet(StringComparer.Ordinal).SetEquals(extension.RequirementIds) ||
            ContinuedCheckpoint(state, step) is not { InvalidatedTaskIds.Count: 0 } checkpoint) return false;

        var target = new TaskPlanSymbols(state.Plan).Scopes.SingleOrDefault(scope => scope.Path == path);
        var controlBody = target is { Parent: { } parent, Owner: { } owner } && parent.Path == step.Scope &&
            checkpoint.TaskIds.Contains(owner.Id, StringComparer.Ordinal);
        var groupBody = step.Scope == "/root" && target is { Parent: null, Owner: null } &&
            checkpoint.Fragment["groups"] is JsonArray groups && groups.Any(id => path == "/groups/" + id + "/body");
        if (!controlBody && !groupBody) return false;

        // The checkpoint still names the parent as its continuation. Moving down
        // once consumes that opportunity without recording fictitious plan work.
        ContinueAcceptedBlock(state, path);
        RetainPrerequisiteEvidence(state, preserveConsumedHistory: true, continuedScope: path);
        return true;
    }

    private static void ContinueAcceptedBlock(PlanningSession state, string path)
    {
        var step = CloneStep(State(state).CurrentStep!);
        if (path != step.Scope) { step.Inputs.Clear(); step.DependencyTaskIds.Clear(); }
        step.Scope = path;
        step.ContinuationBinding = null; step.BoundaryTransitions = null;
        var probe = CloneTransitionState(state);
        if (State(probe).ReplacementScope is not null) State(probe).ReplacementScope = path;
        ValidateSelection(probe, step);
        State(state).CurrentStep = step;
    }
}
