using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningPrerequisites
{
    internal static List<PlanningDiagnostic> Read(PlanningSession state, JsonArray blockers)
    {
        var actions = SemanticPlanning.Actions(state.SemanticPlan!).ToDictionary(a => a.Id, StringComparer.Ordinal);
        var scope = state.BindingProgress?.CurrentActions is { Count: > 0 } batch
            ? SemanticPlanning.Actions(new() { Actions = state.SemanticPlan!.Actions.Where(a => batch.Contains(a.Id)).ToList(),
                Subflows = state.BindingProgress.CompletedActions.Count == 0 ? state.SemanticPlan.Subflows : [] }).Select(a => a.Id).ToHashSet(StringComparer.Ordinal)
            : actions.Keys.ToHashSet(StringComparer.Ordinal);
        var result = new List<PlanningDiagnostic>();
        foreach (var blocker in blockers)
        {
            var id = blocker!["actionId"]!.GetValue<string>();
            if (!scope.Contains(id)) throw Invalid("The blocker is outside the issued binding scope.");
            var context = blocker["prerequisite"] is { } value ? JsonSerializer.Deserialize(value, PlanningJsonContext.Default.PlanningPrerequisiteContext) : null;
            if (context is not null)
            {
                if (context.Kind is not ("missing_observation" or "missing_artifact" or "unavailable_outcome" or "blocked_dependency") || string.IsNullOrWhiteSpace(context.Description))
                    throw Invalid("A prerequisite needs a supported kind and an explanation.");
                if (context.Output is { } output && !actions[id].Outputs.Any(o => o.Name == output)) throw Invalid("The affected business output does not exist.");
                if (context.RootActionId is { } root && (root == id || !actions.ContainsKey(root) || !blockers.Any(b => b!["actionId"]!.ToString() == root)))
                    throw Invalid("A dependent blocker must reference a distinct reported root action.");
                if (context.Kind == "blocked_dependency" && context.RootActionId is null) throw Invalid("A dependent blocker must identify its root action.");
                if (context.ConsumerCapability is { } capability)
                {
                    if (state.Grounding?.Selections?.SingleOrDefault(s => s.ActionId == id)?.CapabilityIds.Contains(capability) != true)
                        throw Invalid("The consumer was not selected for this action.");
                    if (context.ContractPath is { } path && !HasPath(PlanningCapabilityArguments.EditableArguments(state.Catalog!.Capabilities.Single(c => c.Id == capability)), path))
                        throw Invalid("The consumer contract path is not declared.");
                }
                else if (context.ContractPath is not null) throw Invalid("A contract path requires an issued consumer capability.");
            }
            result.Add(new("SEMANTIC_BINDING_BLOCKED", "/actions/" + id, blocker["reason"]!.GetValue<string>(), ValidationStage: "grounding") { Prerequisite = context });
        }
        foreach (var diagnostic in result)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { diagnostic.Location };
            var current = diagnostic;
            while (current.Prerequisite?.RootActionId is { } root)
            {
                if (!visited.Add("/actions/" + root)) throw Invalid("Prerequisite causes must not form a cycle.");
                current = result.Single(d => d.Location == "/actions/" + root);
            }
        }
        return result;
    }

    private static bool HasPath(JsonObject schema, string path)
    {
        if (!path.StartsWith('/') || path.Length == 1) return false;
        JsonNode? node = schema;
        foreach (var part in path.Split('/').Skip(1))
        {
            node = node?["properties"]?[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
            if (node is null) return false;
        }
        return true;
    }

    internal static bool Technical(PlanningDiagnostic diagnostic) => diagnostic.Code == "SEMANTIC_BINDING_BLOCKED" &&
        diagnostic.Prerequisite?.Kind != "unavailable_outcome";
    private static PlanningResponseException Invalid(string message) => new([new("BINDING_BLOCKER_INVALID", "/blockedActions", message)]);
}
