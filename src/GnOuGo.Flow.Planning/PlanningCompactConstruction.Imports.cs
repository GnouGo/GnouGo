using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private sealed record ActualImport(string Location, string Scope, string Phase, TaskValue Value);

    internal static bool UsesDerivedImports(PlanningSession state, PlanningConstructionCandidate candidate) =>
        PlanningConstructionPhasePolicies.IsFresh(state.Request) && candidate.AuthorizedPaths is null && state.EditablePaths is null;

    internal static JsonArray ActualImportedInterfaces(PlanningSession state) => ActualImportedInterfaces(state, null);

    private static JsonArray ActualImportedInterfaces(PlanningSession state, ReviewInterfaces? verified)
    {
        var candidate = state.Construction!.Candidate!;
        var imports = verified?.Imports ?? ActualImports(candidate);
        if (imports.Count == 0) return [];
        var validation = verified?.Candidate ?? ValidateReviewInterfaces(state, candidate.Plan, candidate.OpenScopes,
            new Dictionary<string, IReadOnlyList<TaskValue>>(), ImportRequests(imports));
        var result = new JsonArray();
        foreach (var group in imports.GroupBy(import => import.Location, StringComparer.Ordinal))
        {
            var exact = group.ToArray();
            if (!validation.Interfaces.TryGetValue(group.Key, out var bound) || bound.Count != exact.Length)
                PlanningConstruction.Fail("CONSTRUCTION_STEP_INPUT_INVALID", group.Key,
                    "Every actual draft reference must resolve at its consumer before scoped review.");
            for (var index = 0; index < exact.Length; index++)
                result.Add((JsonNode)new JsonObject
                {
                    ["consumerLocation"] = exact[index].Location, ["scope"] = exact[index].Scope, ["phase"] = exact[index].Phase,
                    ["binding"] = PlanningJsonTransport.TaskPlanPart(PlanningConstruction.Node(bound![index].Value, PlanningJsonContext.Default.TaskValue)),
                    ["schema"] = bound[index].Schema.DeepClone()
                });
        }
        return result;
    }

    private static IReadOnlyList<ActualImport> ActualImports(PlanningConstructionCandidate candidate)
    {
        var symbols = new TaskPlanSymbols(candidate.Plan);
        var added = candidate.TaskIds.ToHashSet(StringComparer.Ordinal);
        var imports = new List<ActualImport>();
        foreach (var site in symbols.Values.Values)
        {
            var taskId = site.Path.Split('/') is ["", "tasks", var id, ..] ? id : null;
            var addedTask = taskId is not null && added.Contains(taskId);
            var selectedExport = site.Path.StartsWith(candidate.Extension.Scope + "/outputs/", StringComparison.Ordinal) &&
                (candidate.Extension.Outputs.Any(output => site.Path == candidate.Extension.Scope + "/outputs/" + output.Name) ||
                 candidate.Extension.CloseScopes.Contains(candidate.Extension.Scope, StringComparer.Ordinal));
            if (!addedTask && !selectedExport) continue;
            var phase = addedTask ? symbols.Phase(taskId!) : "outputs";
            foreach (var value in References(site.Value))
                imports.Add(new(site.Path, site.Scope.Path, phase, value));
        }
        return imports.DistinctBy(import => (import.Location, Identity(import.Value))).ToList();

        IEnumerable<TaskValue> References(TaskValue value)
        {
            // Keep the complete field path. Its producer's unused object fields
            // never become imported merely because this projection reads one field.
            if (value.Kind == "field" && value.Items.Count == 1 && IsExternalProjection(value.Items[0]) || IsExternalReference(value))
            { yield return value; yield break; }
            foreach (var child in value.Items.Concat(value.Members.Select(member => member.Value)))
                foreach (var reference in References(child)) yield return reference;
        }
        bool IsExternalProjection(TaskValue value) => IsExternalReference(value) ||
            value.Kind == "field" && value.Items.Count == 1 && IsExternalProjection(value.Items[0]);
        bool IsExternalReference(TaskValue value) => value.Kind is "input" or "item" or "index" or "choice" ||
            value.Kind is "output" or "present" && !added.Contains(value.Source ?? "");
        static string Identity(TaskValue value) => JsonSerializer.Serialize(value, PlanningJsonContext.Default.TaskValue);
    }

    private sealed record ReviewInterfaces(IReadOnlyList<ActualImport> Imports,
        TaskConstructionValidation Candidate, TaskConstructionValidation? Accepted);

    // Aggregate exact consumers/frontiers before preflight. These results live
    // only for this context construction; no validation result is persisted or
    // reused across candidate, contract or open-scope changes.
    private static ReviewInterfaces VerifiedReviewInterfaces(PlanningSession state, bool candidateScope)
    {
        var construction = state.Construction!;
        var candidate = construction.Candidate!;
        var step = State(state).CurrentStep!;
        var imports = ActualImports(candidate);
        var candidateInterfaces = new Dictionary<string, IReadOnlyList<TaskValue>>(StringComparer.Ordinal);
        var acceptedInterfaces = new Dictionary<string, IReadOnlyList<TaskValue>>(StringComparer.Ordinal);
        var scopePlan = candidateScope ? candidate.Plan : state.Plan;
        if (scopePlan is not null && new TaskPlanSymbols(scopePlan).Scopes.SingleOrDefault(s => s.Path == step.Scope) is { } scope &&
            (candidateScope || scope.Source.Outputs.Count > 0))
            (candidateScope ? candidateInterfaces : acceptedInterfaces)[step.Scope + "/always"] =
                scope.Source.Outputs.Take(8).Select(o => o.Value).ToArray();
        var ownerId = ScopeGroup(state, step.Scope)?.Id;
        foreach (var id in new[] { step.GroupId, ownerId }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            // Review the owning group at the candidate boundary; called producer
            // groups retain their accepted interfaces. Reuse this same preflight
            // for the displayed declarations, exports and scope facts.
            var candidateGroup = UsesDecisionView(state) && id == ownerId;
            var groupPlan = candidateGroup ? candidate.Plan : state.Plan;
            var groupScopes = candidateGroup ? candidate.OpenScopes : construction.OpenScopes;
            var path = "/groups/" + id + "/body";
            if (groupPlan?.Groups.SingleOrDefault(g => g.Id == id) is { } group &&
                !groupScopes.Any(s => s == path || s.StartsWith(path + "/", StringComparison.Ordinal)))
                (candidateGroup ? candidateInterfaces : acceptedInterfaces)[path] = group.Body.Outputs.Select(o => o.Value).ToArray();
        }
        var bindings = ImportRequests(imports);
        if (state.Plan is not null && PlanningConstruction.Hash(state.Plan) == PlanningConstruction.Hash(candidate.Plan) &&
            construction.OpenScopes.ToHashSet(StringComparer.Ordinal).SetEquals(candidate.OpenScopes))
        {
            foreach (var requested in acceptedInterfaces) candidateInterfaces[requested.Key] = requested.Value;
            var shared = ValidateReviewInterfaces(state, candidate.Plan, candidate.OpenScopes, candidateInterfaces, bindings);
            return new(imports, shared, shared);
        }
        var validated = ValidateReviewInterfaces(state, candidate.Plan, candidate.OpenScopes, candidateInterfaces, bindings);
        var accepted = acceptedInterfaces.Count == 0 ? null : ValidateReviewInterfaces(state, state.Plan!, construction.OpenScopes,
            acceptedInterfaces, null);
        return new(imports, validated, accepted);
    }

    private static Dictionary<string, IReadOnlyList<TaskValue>> ImportRequests(IReadOnlyList<ActualImport> imports) =>
        imports.GroupBy(import => import.Location, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => (IReadOnlyList<TaskValue>)group.Select(import => import.Value).ToArray(), StringComparer.Ordinal);

    private static TaskConstructionValidation ValidateReviewInterfaces(PlanningSession state, TaskPlan plan, IEnumerable<string> open,
        IReadOnlyDictionary<string, IReadOnlyList<TaskValue>> interfaces,
        IReadOnlyDictionary<string, IReadOnlyList<TaskValue>>? bindings)
    {
        var probe = PlanningConstruction.Clone(plan);
        foreach (var choice in probe.Choices.Where(choice => choice.Selected is null)) choice.Selected = choice.Recommended;
        var boundaries = open.ToHashSet(StringComparer.Ordinal);
        if (interfaces.Count <= 8 && interfaces.Values.Sum(values => (long)values.Count) <= 128)
            return Validate(interfaces, bindings);
        // Combining previously independent frontiers must not reduce their
        // existing capacity. Keep each compiler request within its unchanged
        // limit; an indivisible oversized frontier still fails explicitly.
        var batches = new List<Dictionary<string, IReadOnlyList<TaskValue>>> { new(StringComparer.Ordinal) };
        foreach (var frontier in interfaces)
        {
            var batch = batches[^1];
            if (batch.Count > 0 && (batch.Count == 8 || batch.Values.Sum(values => (long)values.Count) + frontier.Value.Count > 128))
                batches.Add(batch = new(StringComparer.Ordinal));
            batch.Add(frontier.Key, frontier.Value);
        }
        var resolved = new Dictionary<string, IReadOnlyList<TaskPlanCompiler.RepairSource>>(StringComparer.Ordinal);
        foreach (var batch in batches)
        {
            foreach (var exact in Validate(batch, bindings).Interfaces) resolved.Add(exact.Key, exact.Value);
            bindings = null;
        }
        return new([], resolved);

        TaskConstructionValidation Validate(IReadOnlyDictionary<string, IReadOnlyList<TaskValue>> frontiers,
            IReadOnlyDictionary<string, IReadOnlyList<TaskValue>>? consumers)
        {
            var validation = TaskPlanCompiler.ValidateConstruction(probe, state.Catalog!, state.Request, boundaries, frontiers, consumers);
            if (validation.Diagnostics.Count > 0) throw new PlanningResponseException(validation.Diagnostics.ToList());
            return validation;
        }
    }

}
