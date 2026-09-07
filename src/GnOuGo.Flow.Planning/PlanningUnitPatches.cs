using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Repair coordinates come from schema/validator evidence, never from model-selected scope.</summary>
internal sealed class PlanningUnitPatches(JsonObject schema, Dictionary<string, string[]> slots, List<string[]> removals)
{
    internal JsonObject Schema { get; } = schema;

    internal static PlanningUnitPatches Create(PlanningGraph graph, PlanningConstructionUnit unit, JsonObject full)
    {
        var all = new Dictionary<string, (string[] Parts, JsonNode Schema)>(StringComparer.Ordinal);
        var extra = new List<string[]>();
        void Add(string[] parts, JsonNode schema) => all.Add(string.Join("/", parts.Select(PlanningSchemaReferences.Escape)), (parts, schema));
        var root = full["properties"]!.AsObject();
        foreach (var (key, value) in root)
        {
            if (key == "functions") { Add([key], value!); continue; }
            foreach (var (name, fields) in value!["properties"]!.AsObject())
                foreach (var (field, shape) in fields!["properties"]!.AsObject()) Add([key, name, field], shape!);
        }
        void Unknown(JsonObject? value, JsonObject properties, string[] path, int depth)
        {
            if (value is null) return;
            foreach (var (key, child) in value)
                if (!properties.ContainsKey(key)) extra.Add(path.Append(key).ToArray());
                else if (depth < 2 && key != "functions" && properties[key]?["properties"] is JsonObject nested)
                    Unknown(child as JsonObject, nested, path.Append(key).ToArray(), depth + 1);
        }
        Unknown(unit.Candidate, root, [], 0);
        var wi = graph.Workflows.FindIndex(w => w.Key == unit.WorkflowKey);
        var workflow = graph.Workflows[wi]; var graphRoot = "/workflows/" + wi;
        var located = PlanningGraphValidation.Located(workflow.Steps, graphRoot + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, graphRoot + "/finally")).ToArray();
        var selected = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var changes = new JsonObject();
        foreach (var (key, slot) in all)
        {
            var expected = slot.Schema.DeepClone().AsObject(); expected["$defs"] = full["$defs"]!.DeepClone();
            var invalid = !Read(unit.Candidate, slot.Parts, out var value) || PlanningContractValidation.ValidateInstance(value, expected).Count != 0;
            var graphPath = slot.Parts[0] switch
            {
                "functions" => graphRoot + "/functions",
                "nodes" => located.First(n => n.Node.Key == slot.Parts[1]).Path + "/" + (slot.Parts[2] == "context" ? "input" : slot.Parts[2]),
                "inputs" => graphRoot + "/inputs/" + workflow.Inputs.FindIndex(p => p.Name == slot.Parts[1]) + "/" + slot.Parts[2],
                _ => graphRoot + "/outputs/" + workflow.Outputs.FindIndex(p => p.Name == slot.Parts[1]) + "/" + slot.Parts[2]
            };
            var diagnosed = unit.Diagnostics.Any(d => d.Location == graphPath || d.Location.StartsWith(graphPath + "/", StringComparison.Ordinal));
            if (invalid || diagnosed) { selected[key] = slot.Parts; changes[key] = slot.Schema.DeepClone(); }
        }
        // A conversion-level failure cannot establish a smaller field scope. The unit remains the boundary.
        if (selected.Count == 0 && extra.Count == 0)
            foreach (var (key, slot) in all) { selected[key] = slot.Parts; changes[key] = slot.Schema.DeepClone(); }
        JsonObject Obj(JsonObject properties) => new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false };
        var remove = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = extra.Count, ["maxItems"] = extra.Count };
        if (extra.Count > 0) remove["items"]!["enum"] = new JsonArray(extra.Select(p => (JsonNode?)JsonValue.Create(string.Join("/", p.Select(PlanningSchemaReferences.Escape)))).ToArray());
        var result = Obj(new() { ["changes"] = Obj(changes), ["remove"] = remove }); result["$defs"] = full["$defs"]!.DeepClone();
        PlanningConstruction.PruneDefinitions(result);
        return new(result, selected, extra);
    }

    internal JsonObject Apply(JsonObject? candidate, JsonObject? response)
    {
        var errors = PlanningContractValidation.ValidateInstance(response, Schema);
        if (errors.Count != 0) throw new InvalidOperationException("Invalid targeted patch: " + string.Join("; ", errors));
        var result = candidate?.DeepClone().AsObject() ?? new JsonObject();
        var removed = response!["remove"]!.AsArray().Select(v => v!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        if (!removed.SetEquals(removals.Select(p => string.Join("/", p.Select(PlanningSchemaReferences.Escape))))) throw new InvalidOperationException("Remove only the diagnosed extra construction fields.");
        foreach (var path in removals)
            if (Read(result, path[..^1], out var parent) && parent is JsonObject obj) obj.Remove(path[^1]);
        foreach (var (key, path) in slots)
        {
            var target = result;
            foreach (var part in path[..^1])
            {
                if (target[part] is not JsonObject) target[part] = new JsonObject();
                target = target[part]!.AsObject();
            }
            target[path[^1]] = response["changes"]![key]?.DeepClone();
        }
        return result;
    }

    private static bool Read(JsonNode? root, string[] path, out JsonNode? value)
    {
        value = root;
        foreach (var part in path) if (value is not JsonObject obj || !obj.TryGetPropertyValue(part, out value)) return false;
        return true;
    }
}
