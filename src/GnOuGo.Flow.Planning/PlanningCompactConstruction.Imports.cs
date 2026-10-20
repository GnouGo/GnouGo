using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static partial class PlanningCompactConstruction
{
    private sealed record ActualImport(string Location, string Scope, string Phase, TaskValue Value);

    internal static bool UsesDerivedImports(PlanningSession state, PlanningConstructionCandidate candidate) =>
        PlanningConstructionPhasePolicies.IsFresh(state.Request) && candidate.AuthorizedPaths is null && state.EditablePaths is null;

    internal static JsonArray ActualImportedInterfaces(PlanningSession state)
    {
        var candidate = state.Construction!.Candidate!;
        var plan = PlanningConstruction.Clone(candidate.Plan);
        foreach (var choice in plan.Choices.Where(choice => choice.Selected is null)) choice.Selected = choice.Recommended;
        var symbols = new TaskPlanSymbols(plan);
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
        imports = imports.DistinctBy(import => (import.Location, Identity(import.Value))).ToList();
        if (imports.Count == 0) return [];
        var requested = imports.GroupBy(import => import.Location, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<TaskValue>)group.Select(import => import.Value).ToArray(), StringComparer.Ordinal);
        var validation = TaskPlanCompiler.ValidateConstruction(plan, state.Catalog!, state.Request,
            candidate.OpenScopes.ToHashSet(StringComparer.Ordinal), requestedBindings: requested);
        if (validation.Diagnostics.Count > 0) throw new PlanningResponseException(validation.Diagnostics.ToList());
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
}
