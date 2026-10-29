using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    // A retained claim can propose final review; it cannot establish business
    // closure or authority for another transition. Rollback invalidates these
    // checkpoint associations along with the affected requirement.
    internal static bool RequirementCompletionClaimed(PlanningSession state, string id)
        => state.Construction!.Requirements.Single(item => item.Id == id).Status == "closed" || CompletionClaims(state, id).Any();

    internal static bool CanClaimRequirementCompletion(PlanningSession state, string id)
        => state.Construction!.Requirements.Single(item => item.Id == id).Status != "closed" &&
            !CompletionClaims(state, id).Any(checkpoint => checkpoint.PlanHash == PlanningConstruction.Hash(state.Plan));

    private static IEnumerable<PlanningConstructionCheckpoint> CompletionClaims(PlanningSession state, string id)
    {
        var construction = state.Construction!;
        var requirement = construction.Requirements.Single(item => item.Id == id);
        return construction.Checkpoints.Where(checkpoint =>
            checkpoint.InvalidatedTaskIds.Count == 0 && requirement.Checkpoints.Contains(checkpoint.Sequence) &&
            checkpoint.RequirementIds.Contains(id, StringComparer.Ordinal) &&
            checkpoint.ClosedRequirementIds?.Contains(id, StringComparer.Ordinal) == true &&
            checkpoint.Step?.CloseRequirementIds.Contains(id, StringComparer.Ordinal) == true);
    }

    internal static bool CompletionOnly(PlanningSession state) =>
        state.Construction!.Requirements.Count > 0 && state.Construction.Requirements.All(requirement => RequirementCompletionClaimed(state, requirement.Id)) &&
        State(state).ReplacementScope is null && !state.Diagnostics.Any(diagnostic => diagnostic.Required) &&
        state.Plan is { } plan && plan.Choices.All(choice => choice.Selected is not null) &&
        State(state).Sources.All(source => source.Kind is not ("page" or "answer_page" or "revision_page") || source.Indexed) &&
        IntentDeclarationsComplete(State(state)) &&
        State(state).SuspendedSteps?.Any(step => step.Status is "waiting" or "resuming") != true &&
        (state.Requirements?.Outputs ?? []).Where(output => output.Required).All(output => plan.Root.Outputs.Any(binding => binding.Name == output.Name)) &&
        state.Construction.RebuildTasks.All(task => new TaskPlanSymbols(plan).Tasks.ContainsKey(task.Id));

    private static int OpenDescendants(PlanningSession state, TaskPlanSymbols symbols, string path) =>
        state.Construction!.OpenScopes.Count(open =>
        {
            for (var parent = symbols.Scopes.SingleOrDefault(scope => scope.Path == open)?.Parent; parent is not null; parent = parent.Parent)
                if (parent.Path == path) return true;
            return false;
        });

    private static bool ScopeWithin(TaskPlan plan, string path, string boundary)
    {
        for (var scope = new TaskPlanSymbols(plan).Scopes.SingleOrDefault(scope => scope.Path == path); scope is not null; scope = scope.Parent)
            if (scope.Path == boundary) return true;
        return false;
    }

    // This prepares only a structural candidate. The caller still runs the ordinary
    // preflight, final output checks when applicable, and mandatory scoped review.
    internal static PlanningSession? PrepareSelectedClosure(PlanningSession state)
    {
        if (state.Plan is null || state.PendingCall is not null ||
            state.Construction!.Candidate is not null || state.EditablePaths is not null || state.Diagnostics.Any(item => item.Required)) return null;
        var construction = state.Construction!;
        var compact = State(state);
        if (compact.CurrentStep is not { Kind: "close", DiscoveryComplete: true, CloseScopes: not null } step ||
            step.BaseHash != PlanningConstruction.Hash(state.Plan) || compact.ReplacementScope is not null ||
            compact.ResumingStepId is not null || step.ContinuationBinding is not null ||
            step.Inputs.Count > 0 || step.DependencyTaskIds.Count > 0 || step.ExpectedOutputs.Count > 0 ||
            compact.PendingCommitments.Any(item => item.Value.Count > 0)) return null;
        // Evidence notes remain attached to the selected claim for business review.
        // They do not add executable content for another model call to generate.
        if (construction.RebuildTasks.Any(task => !new TaskPlanSymbols(state.Plan).Tasks.ContainsKey(task.Id))) return null;
        ValidateSelectedCloseScopes(state, step);
        // Selection names desired public outputs, not their value expressions.
        // Only an already accepted binding can be preserved without a draft.
        if (step.OutputNames.Any(name => state.Plan.Root.Outputs.All(output => output.Name != name)) ||
            step.CloseScopes.Contains("/root", StringComparer.Ordinal) &&
                ((state.Requirements!.Outputs ?? []).Any(output => output.Required && state.Plan.Root.Outputs.All(binding => binding.Name != output.Name)) ||
                 state.Plan.Choices.Any(choice => choice.Selected is null) || compact.SuspendedSteps?.Any(Active) == true) ||
            step.CloseScopes.Count == 0 && !step.CloseRequirementIds.Any(id => CanClaimRequirementCompletion(state, id)))
            return null;
        var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession),
            PlanningJsonContext.Default.PlanningSession)!;
        probe.Construction!.Candidate = PlanningConstruction.Apply(probe, new()
        {
            Scope = step.Scope, RequirementIds = step.RequirementIds.ToList(), CloseScopes = step.CloseScopes.ToList(),
            Complete = step.CloseScopes.Contains("/root", StringComparer.Ordinal)
        });
        return probe;
    }

    internal static void ValidateSelectedCloseScopes(PlanningSession state, PlanningConstructionStep step)
    {
        if (step.Kind != "close")
        {
            if (step.CloseScopes is not null)
                PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/closeScopes", "Only an explicit close selection chooses a closure target.");
            return;
        }
        if (step.CloseScopes is null || step.CloseScopes.Count > 1 ||
            !state.Construction!.OpenScopes.Contains(step.Scope, StringComparer.Ordinal) ||
            step.CloseScopes.Any(path => path != step.Scope) ||
            step.CloseScopes.Count == 1 && !ClosurePresentation(state, step).CloseScope)
            PlanningConstruction.Fail("CONSTRUCTION_STEP_INVALID", "/step/closeScopes", "Choose requirement-only closure or the selected scope when its existing closure conditions permit it.");
    }

    internal static PlanningSession? PrepareReviewedClosure(PlanningSession state)
    {
        var construction = state.Construction!;
        var compact = State(state);
        if (!CompletionOnly(state) || state.PendingCall is not null || construction.Candidate is not null || compact.CurrentStep is not null)
            return null;
        var symbols = new TaskPlanSymbols(state.Plan!);
        // A path's textual depth is not its scope depth: task IDs are global.
        var boundary = construction.OpenScopes.FirstOrDefault(path => OpenDescendants(state, symbols, path) == 0 &&
            (path != "/root" || construction.OpenScopes.Count == 1));
        if (boundary is null) return null;
        var requirements = ClosureRequirements(state, symbols, boundary);
        // Mechanical closure must not substitute an unrelated first obligation
        // or silently omit a wider original set to fit the existing step bound.
        if (requirements.Length < 1) return null;
        var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession),
            PlanningJsonContext.Default.PlanningSession)!;
        var next = probe.Construction!.Compact!;
        next.CurrentStep = new()
        {
            Id = "step_" + (++next.NextStep), BaseHash = PlanningConstruction.Hash(state.Plan), Scope = boundary, Kind = "close",
            Objective = "Close this technically complete boundary; final business review still checks all original obligations.",
            RequirementIds = requirements.ToList(), DiscoveryComplete = true,
            CloseRequirementIds = requirements.ToList(), CloseScopes = [boundary]
        };
        probe.Construction!.Candidate = PlanningConstruction.Apply(probe, new()
        {
            Scope = boundary, CloseScopes = [boundary], Complete = boundary == "/root",
            RequirementIds = next.CurrentStep.RequirementIds.ToList()
        });
        probe.Construction.Candidate.Reviews.Add(new JsonObject
            {
                ["purpose"] = "construction_transition", ["outcome"] = "prepared_closure",
                ["reason"] = "All original obligations are claimed complete; close one technically ready leaf before mandatory final review.",
                ["baseline"] = next.CurrentStep.BaseHash, ["scope"] = boundary,
                ["requirements"] = new JsonArray(requirements.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["checkpoint"] = construction.Checkpoints.LastOrDefault()?.Sequence
            });
        return probe;
    }

    internal static PlanningSession? PrepareAcceptedTransition(PlanningSession state)
    {
        return AcceptedTransitions(state).FirstOrDefault()?.State;
    }

    private sealed record AcceptedTransition(string Kind, string Reason, int? Checkpoint, PlanningSession State);

    // One request-local view owns transition precedence and applicability. It is
    // derived from existing checkpoints, never a persisted queue or another plan.
    private static IEnumerable<AcceptedTransition> AcceptedTransitions(PlanningSession state)
    {
        if (state.PendingCall is not null || state.Construction!.Candidate is not null || State(state).CurrentStep is not null ||
            state.EditablePaths is not null || State(state).ReplacementScope is not null || state.Diagnostics.Any(item => item.Required)) yield break;
        var resumed = CloneTransitionState(state);
        ReconcileContinuations(resumed);
        if (!JsonNode.DeepEquals(PlanningConstruction.Node(State(state), PlanningJsonContext.Default.PlanningCompactConstructionState),
            PlanningConstruction.Node(State(resumed), PlanningJsonContext.Default.PlanningCompactConstructionState)))
        {
            yield return new("navigation", "invalidate_stale_prerequisite_navigation", null, resumed);
            yield break;
        }
        if (TryResumeReviewedConsumer(resumed))
        {
            yield return new("consumer", "exact_reviewed_binding", resumed.Construction!.Checkpoints.LastOrDefault()?.Sequence, resumed);
            yield break;
        }
        // A retained consumer has precedence; do not close its boundary while its
        // exact missing value or its review is unresolved.
        foreach (var checkpoint in state.Construction.Checkpoints.AsEnumerable().Reverse())
            if (PrepareReviewedBoundaryTransition(state, checkpoint) is { } boundary)
                yield return new("boundary", "exact_reviewed_boundary_claim", checkpoint.Sequence, boundary);
        if (PrepareReviewedClosure(state) is { } closure)
            yield return new("closure", "all_original_obligations_claimed", null, closure);
    }

    internal static JsonObject SelectionDecision(PlanningSession state, JsonObject? continuation = null)
    {
        var compact = State(state);
        var reason = state.Diagnostics.Any(d => d.Required) ? "resolve_retained_diagnostic" :
            compact.SuspendedSteps?.LastOrDefault(item => item.Status == "waiting") is not null ? "resolve_unproven_consumer_binding" :
            CompletionOnly(state) ? "resolve_unproven_closure" : "select_next_business_action";
        var result = new JsonObject { ["kind"] = "semantic_selection", ["reason"] = reason,
            ["baseline"] = PlanningConstruction.Hash(state.Plan),
            ["acceptedCheckpoint"] = state.Construction!.Checkpoints.LastOrDefault()?.Sequence ?? 0 };
        {
            if (LocalComputationEligibility(state) is { } local)
                result["localComputations"] = local;
            if (reason == "resolve_unproven_consumer_binding" && continuation?["status"]?.ToString() == "waiting")
            {
                result["reason"] = "resolve_current_prerequisite";
                result["continuationId"] = continuation["id"]!.DeepClone();
                result["prerequisite"] = continuation["prerequisite"]!.DeepClone();
            }
        }
        result["reviewedTransitions"] = new JsonArray(AcceptedTransitions(state).Take(4).Select(item => (JsonNode)new JsonObject
            { ["kind"] = item.Kind, ["reason"] = item.Reason, ["checkpoint"] = item.Checkpoint }).ToArray());
        return result;
    }

    private static JsonObject? LocalComputationEligibility(PlanningSession state) =>
        state.Catalog is { } catalog
            ? new JsonObject
            {
                ["basis"] = "catalog_eligibility_only", ["value"] = catalog.AllowedStepTypes.Contains("set"),
                ["extract"] = catalog.AllowedStepTypes.Contains("mapping.dynamic"),
                ["interpret"] = catalog.AllowedStepTypes.Contains("llm.call") && catalog.AllowedStepTypes.Contains("template.render")
            } : null;

    private static string[] ClosureRequirements(PlanningSession state, TaskPlanSymbols symbols, string boundary)
    {
        var originals = state.Requirements!.Outcomes;
        if (boundary == "/root") return originals.Select(requirement => requirement.Id).ToArray();
        var scope = symbols.Scopes.Single(item => item.Path == boundary);
        var tasks = symbols.Tasks.Where(item => Within(item.Value.Scope)).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        for (var owner = scope; owner is not null; owner = owner.Parent)
            if (owner.Owner is { } task) tasks.Add(task.Id);
        var publicOutputs = state.Plan!.Root.Outputs.Where(output => TaskPlanCompiler.Values(output.Value)
            .Any(value => value.Kind is "output" or "present" && value.Source is { } id && tasks.Contains(id)))
            .Select(output => output.Name).ToHashSet(StringComparer.Ordinal);
        var groupId = state.Plan.Groups.SingleOrDefault(group => ReferenceEquals(group.Body, scope.Source))?.Id;
        var checkpoints = state.Construction!.Checkpoints.Where(checkpoint =>
            checkpoint.TaskIds.Any(tasks.Contains) || checkpoint.ImportedTaskIds.Any(tasks.Contains) ||
            checkpoint.Step is { } step && symbols.Scopes.SingleOrDefault(item => item.Path == step.Scope) is { } selected && Within(selected) ||
            groupId is not null && checkpoint.Fragment["groups"] is JsonArray groups && groups.Any(group => group?.ToString() == groupId) ||
            checkpoint.Fragment["scope"]?.ToString() == "/root" && checkpoint.Fragment["outputs"] is JsonArray outputs &&
                outputs.Any(output => output?["name"] is JsonValue name && publicOutputs.Contains(name.ToString()))).ToArray();
        // Work without an accepted association needs explicit
        // selection; task type or objective prose cannot invent that association.
        if (tasks.Any(id => !checkpoints.Any(checkpoint => checkpoint.TaskIds.Contains(id, StringComparer.Ordinal)))) return [];
        var ids = checkpoints.SelectMany(checkpoint => checkpoint.RequirementIds).ToHashSet(StringComparer.Ordinal);
        if (ids.Any(id => originals.All(requirement => requirement.Id != id))) return [];
        return originals.Where(requirement => ids.Contains(requirement.Id)).Select(requirement => requirement.Id).ToArray();

        bool Within(TaskPlanSymbols.Scope item)
        {
            for (var current = item; current is not null; current = current.Parent)
                if (current.Path == boundary) return true;
            return false;
        }
    }

    private static JsonObject AcceptedExportDirectory(PlanningSession state, TaskPlanSymbols.Scope scope)
    {
        var version = ScopeInterfaceVersion(state.Plan!, state.Construction!.OpenScopes, scope.Path);
        return new()
        {
            ["basis"] = "accepted_declarations", ["count"] = scope.Source.Outputs.Count,
            ["names"] = new JsonArray(scope.Source.Outputs.Take(8).Select(output => (JsonNode?)JsonValue.Create(output.Name)).ToArray()),
            ["evidence"] = ScopeInterfaceAddress(scope.Path, false, version, 0)
        };
    }
}
