using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    internal static bool CompletionOnly(PlanningSession state) => UsesConvergentConstruction(state) &&
        state.Construction!.Requirements.Count > 0 && state.Construction.Requirements.All(requirement => requirement.Status == "closed") &&
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
    internal static PlanningSession? PrepareReviewedClosure(PlanningSession state)
    {
        var construction = state.Construction!;
        var compact = State(state);
        if (!CompletionOnly(state) || state.PendingCall is not null || construction.Candidate is not null ||
            compact.CurrentStep is not null || construction.Requirements.Any(requirement =>
                !construction.Checkpoints.Any(checkpoint => checkpoint.ClosedRequirementIds?.Contains(requirement.Id, StringComparer.Ordinal) == true &&
                    requirement.Checkpoints.Contains(checkpoint.Sequence) && checkpoint.InvalidatedTaskIds.Count == 0 &&
                    PlanningConstructionReviewCoverage.Reviewed(state, checkpoint, PlanningConstruction.LocalPurpose) &&
                    (!checkpoint.NeedsIntermediateReview || PlanningConstructionReviewCoverage.Reviewed(state, checkpoint, PlanningConstruction.IntermediatePurpose)) &&
                    checkpoint.Reviews.Any(review => review["candidateHash"]?.ToString() == checkpoint.PlanHash &&
                        review["outcome"]?.ToString() == "unverified" && review["response"]?["findings"] is JsonArray { Count: 0 }))))
            return null;
        var symbols = new TaskPlanSymbols(state.Plan!);
        // A path's textual depth is not its scope depth: task IDs are global.
        var boundary = construction.OpenScopes.FirstOrDefault(path => OpenDescendants(state, symbols, path) == 0 &&
            (path != "/root" || construction.OpenScopes.Count == 1));
        if (boundary is null) return null;
        var requirements = ClosureRequirements(state, symbols, boundary);
        // Mechanical closure must not substitute an unrelated first obligation
        // or silently omit a wider original set to fit the existing step bound.
        if (requirements.Length is < 1 or > 4) return null;
        var probe = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession),
            PlanningJsonContext.Default.PlanningSession)!;
        var next = probe.Construction!.Compact!;
        next.CurrentStep = new()
        {
            Id = "step_" + (++next.NextStep), BaseHash = PlanningConstruction.Hash(state.Plan), Scope = boundary, Kind = "close",
            Objective = "Close this accepted boundary after its reviewed obligations; preserve all accepted work and exports.",
            RequirementIds = requirements.ToList(), DiscoveryComplete = true
        };
        probe.Construction.CandidateFailureIdentityVersion = 1;
        probe.Construction.Candidate = PlanningConstruction.Apply(probe, new()
        {
            Scope = boundary, CloseScopes = [boundary], Complete = boundary == "/root",
            RequirementIds = next.CurrentStep.RequirementIds.ToList()
        });
        return probe;
    }

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
        // Historical/baseline work without an accepted association needs explicit
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
