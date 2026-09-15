using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Repairs may refine proven contracts, but cannot weaken them to hide a defect.</summary>
internal static class PlanningContractPreservation
{
    internal static bool Preserves(PlanningSnapshot state, PlanningGraph candidate)
    {
        foreach (var progress in state.Construction.Workflows.Where(w => w.Status == "validated"))
        {
            var before = state.Graph!.Workflows.Single(w => w.Key == progress.WorkflowKey);
            var after = candidate.Workflows.Single(w => w.Key == progress.WorkflowKey);
            if (!before.Inputs.All(p => Refines(p.Schema, after.Inputs.Single(q => q.Name == p.Name).Schema)) ||
                !before.Outputs.All(p => Refines(p.Schema, after.Outputs.Single(q => q.Name == p.Name).Schema))) return false;
            var nodes = PlanningGraphCompiler.Enumerate(after.Steps.Concat(after.Finally)).ToDictionary(n => n.Key, StringComparer.Ordinal);
            foreach (var node in PlanningGraphCompiler.Enumerate(before.Steps.Concat(before.Finally)))
                if (!Refines(node.OutputSchema, nodes[node.Key].OutputSchema) ||
                    !Refines(node.StructuredOutput?.Schema, nodes[node.Key].StructuredOutput?.Schema)) return false;
        }
        return true;
        bool Refines(PlanningSchema? previous, PlanningSchema? next) => previous is null || next is not null &&
            RefinesSchema(PlanningGraphCompiler.ToJsonSchema(previous, state.Preparation!), PlanningGraphCompiler.ToJsonSchema(next, state.Preparation!));
    }

    internal static bool RefinesSchema(JsonObject previous, JsonObject next)
    {
        foreach (var key in previous.Select(p => p.Key).Union(next.Select(p => p.Key)))
        {
            if (key == "description" || JsonNode.DeepEquals(previous[key], next[key])) continue;
            if (key is "type" or "enum")
            {
                var before = Values(previous[key]); var after = Values(next[key]);
                if (after.Count > 0 && (before.Count == 0 || after.IsSubsetOf(before))) continue;
            }
            else if (key == "required" && Values(previous[key]).IsSubsetOf(Values(next[key]))) continue;
            else if (key is "items" or "additionalProperties" && previous[key] is JsonObject a && next[key] is JsonObject b && RefinesSchema(a, b)) continue;
            else if (key == "additionalProperties" && next[key] is JsonValue closed && closed.TryGetValue<bool>(out var permits) && !permits) continue;
            else if (key == "properties" && previous[key] is JsonObject properties && next[key] is JsonObject replacements &&
                properties.Count == replacements.Count && properties.All(p => p.Value is JsonObject schema && replacements[p.Key] is JsonObject replacement && RefinesSchema(schema, replacement))) continue;
            return false;
        }
        return true;
    }
    private static HashSet<string> Values(JsonNode? node) => node is JsonArray array
        ? array.Select(n => n!.ToJsonString()).ToHashSet(StringComparer.Ordinal)
        : node is null ? new(StringComparer.Ordinal) : new([node.ToJsonString()], StringComparer.Ordinal);
}
