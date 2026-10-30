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

    internal static HashSet<string> BusinessFragmentKinds(PlanningSession state)
    {
        var step = State(state).CurrentStep!;
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        if (!BlockAnchorConstructed(state) && step.Kind is not (null or "group" or "close" or "choice")) kinds.Add(step.Kind!);
        if (SupportsBusinessFragment(step.Kind))
        {
            kinds.Add("value"); kinds.Add("transform");
            if (step.OperationIds.Count > 0) kinds.Add("operation");
            if (step.GroupId is not null) kinds.Add("call");
            if (BlockCheckpoints(state).FirstOrDefault()?.Step?.Scope is { } origin && origin != step.Scope)
                kinds.UnionWith(NestedControlKinds);
        }
        return kinds;
    }

    internal static bool CanFinishContinuedBlock(PlanningSession state, PlanningConstructionExtension extension) =>
        extension.ContinueInScope is null && state.Construction?.Compact?.CurrentStep is { } step && SupportsBusinessFragment(step.Kind) &&
        BlockCheckpoints(state).Any(checkpoint => checkpoint.Fragment["continueInScope"] is not null);

    internal static List<string> CandidateCompletionClaims(PlanningSession state) =>
        state.Construction!.Candidate?.Extension.ContinueInScope is not null ? [] : State(state).CurrentStep!.CloseRequirementIds;

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

    private static void ValidateBlockContinuation(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (candidate.Extension.ContinueInScope is not { } path) return;
        var step = State(state).CurrentStep!;
        if (!SupportsBusinessFragment(step.Kind) || candidate.AuthorizedPaths is not null || candidate.Extension.Complete ||
            candidate.Extension.CloseScopes.Contains("/root", StringComparer.Ordinal) ||
            candidate.Extension.ContinuationBinding is not null || candidate.Extension.BoundaryTransitions is { Count: > 0 } ||
            !candidate.OpenScopes.Contains(path, StringComparer.Ordinal))
            PlanningConstruction.Fail("CONSTRUCTION_BLOCK_CONTINUATION_INVALID", "/extension/continueInScope",
                "Continue only an unfinished business block in an exact open scope, without completion or deferred consumer/boundary claims.");
        var checkpoints = BlockCheckpoints(state).ToArray();
        var origin = checkpoints.FirstOrDefault()?.Step?.Scope ?? step.Scope;
        var ownedTasks = checkpoints.SelectMany(checkpoint => checkpoint.TaskIds).Concat(candidate.TaskIds).ToHashSet(StringComparer.Ordinal);
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
        ValidateSelection(probe, continuation);
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
