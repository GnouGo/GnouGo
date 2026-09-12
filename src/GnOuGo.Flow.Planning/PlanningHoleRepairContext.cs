using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Current eligible contracts, restricted to retained identities of diagnosed assignments.</summary>
internal static class PlanningHoleRepairContext
{
    internal static JsonObject Create(PlanningSnapshot state, PlanningStagedAssignments staged, IEnumerable<PlanningHole> holes)
    {
        var fields = new JsonObject(); var sources = new JsonObject();
        var workflow = state.Graph!.Workflows.Single(w => w.Key == staged.WorkflowKey);
        foreach (var hole in holes)
        {
            var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
            var eligible = domain.Direct.Concat(domain.Parameters).DistinctBy(b => b.Id).ToDictionary(b => b.Id, StringComparer.Ordinal);
            var parameters = staged.ParameterScopes.GetValueOrDefault(hole.Id) ??
                (staged.Payload["assignments"]?[hole.Id]?["bindings"] as JsonArray)?.Select(p => p!.ToString()).ToList() ?? [];
            var names = new JsonArray();
            foreach (var (name, binding) in staged.Bindings)
            {
                if (!eligible.TryGetValue(PlanningBindingIdentity.Id(binding), out var source)) continue;
                if (!domain.Direct.Any(b => b.Id == source.Id) && !parameters.Contains(name, StringComparer.Ordinal)) continue;
                if (parameters.Contains(name, StringComparer.Ordinal)) names.Add((JsonNode)JsonValue.Create(name)!);
                if (!sources.ContainsKey(name)) sources[name] = new JsonObject
                {
                    ["value"] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(binding, PlanningJsonContext.Default.PlanningValue)),
                    ["schema"] = source.Schema.DeepClone()
                };
            }
            fields[hole.Id] = new JsonObject { ["obligation"] = hole.Purpose, ["expected"] = domain.Contract?.DeepClone(), ["parameters"] = names };
        }
        return new() { ["holes"] = fields, ["bindings"] = sources };
    }
}
