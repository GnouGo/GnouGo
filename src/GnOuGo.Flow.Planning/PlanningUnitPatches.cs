using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Repair coordinates come from schema/validator evidence, never from model-selected scope.</summary>
internal sealed class PlanningUnitPatches(JsonObject schema, Dictionary<string, string[]> slots, List<string[]> removals)
{
    internal JsonObject Schema { get; } = schema;
    internal JsonObject Context(JsonObject? candidate) => new(slots.Select(p =>
        new KeyValuePair<string, JsonNode?>(p.Key, Read(candidate, p.Value, out var value) ? value?.DeepClone() : null)));

    internal PlanningUnitPatches Narrow()
    {
        if (slots.Count <= 1) return this;
        var keep = slots.Take((slots.Count + 1) / 2).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var reduced = Schema.DeepClone().AsObject(); var changes = reduced["properties"]!["changes"]!;
        foreach (var key in slots.Keys.Where(k => !keep.ContainsKey(k))) changes["properties"]!.AsObject().Remove(key);
        changes["required"] = new JsonArray(keep.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        PlanningConstruction.PruneDefinitions(reduced); return new(reduced, keep, removals);
    }

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
                foreach (var (field, shape) in fields!["properties"]!.AsObject())
                    if (field == "arguments")
                        foreach (var (argument, argumentShape) in shape!["properties"]!.AsObject()) Add([key, name, field, argument], argumentShape!);
                    else Add([key, name, field], shape!);
        }
        void Unknown(JsonObject? value, JsonObject properties, string[] path, int depth)
        {
            if (value is null) return;
            foreach (var (key, child) in value)
                if (!properties.ContainsKey(key)) extra.Add(path.Append(key).ToArray());
                else if (depth < 3 && key != "functions" && properties[key]?["properties"] is JsonObject nested)
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
                "nodes" => located.First(n => n.Node.Key == slot.Parts[1]).Path + "/" + (slot.Parts[2] == "context" ? "input" : slot.Parts[2] == "arguments" ? ArgumentPath(slot.Parts) : slot.Parts[2]),
                "inputs" => graphRoot + "/inputs/" + workflow.Inputs.FindIndex(p => p.Name == slot.Parts[1]) + "/" + slot.Parts[2],
                _ => graphRoot + "/outputs/" + workflow.Outputs.FindIndex(p => p.Name == slot.Parts[1]) + "/" + slot.Parts[2]
            };
            var diagnosed = unit.Diagnostics.Any(d => d.Location == graphPath || d.Location.StartsWith(graphPath + "/", StringComparison.Ordinal) || graphPath.StartsWith(d.Location + "/", StringComparison.Ordinal));
            if (invalid || diagnosed)
            {
                var leaves = new List<(string[] Path, JsonNode Shape)>();
                FindValues(value, slot.Parts, graphPath, slot.Schema, leaves);
                if (leaves.Count == 0) leaves.Add((slot.Parts, slot.Schema));
                foreach (var leaf in leaves)
                {
                    var coordinate = string.Join("/", leaf.Path.Select(PlanningSchemaReferences.Escape));
                    selected[coordinate] = leaf.Path; changes[coordinate] = leaf.Shape.DeepClone();
                }
            }
        }

        string ArgumentPath(string[] parts)
        {
            var node = located.Single(n => n.Node.Key == parts[1]).Node;
            var requestIndex = node.Input.Members.FindIndex(m => m.Name == "request");
            var argumentIndex = requestIndex < 0 ? -1 : node.Input.Members[requestIndex].Value.Members.FindIndex(m => m.Name == parts[3]);
            return "input/members/" + requestIndex + "/value/members/" + argumentIndex + "/value";
        }

        void FindValues(JsonNode? value, string[] path, string location, JsonNode shape, List<(string[] Path, JsonNode Shape)> leaves)
        {
            if (value is not JsonObject obj || obj["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var label)) return;
            var before = leaves.Count;
            if (label is "object" or "template" && obj["members"] is JsonArray members)
                for (var i = 0; i < members.Count; i++) SelectChild(members[i]?["value"], path.Concat(["members", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "value"]).ToArray(), location + "/members/" + i + "/value");
            if (label == "array" && obj["items"] is JsonArray items)
                for (var i = 0; i < items.Count; i++) SelectChild(items[i], path.Concat(["items", i.ToString(System.Globalization.CultureInfo.InvariantCulture)]).ToArray(), location + "/items/" + i);
            if (before == leaves.Count && unit.Diagnostics.Any(d => d.Location == location || d.Location.StartsWith(location + "/", StringComparison.Ordinal))) leaves.Add((path, shape));

            void SelectChild(JsonNode? child, string[] childPath, string childLocation)
            {
                var childSchema = new JsonObject { ["$ref"] = "#/$defs/value", ["$defs"] = full["$defs"]!.DeepClone() };
                if (PlanningContractValidation.ValidateInstance(child, childSchema).Count == 0 && !unit.Diagnostics.Any(d => d.Location == childLocation || d.Location.StartsWith(childLocation + "/", StringComparison.Ordinal))) return;
                var count = leaves.Count;
                var reference = new JsonObject { ["$ref"] = "#/$defs/value" };
                FindValues(child, childPath, childLocation, reference, leaves);
                if (leaves.Count == count) leaves.Add((childPath, reference));
            }
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
            JsonNode target = result;
            foreach (var part in path[..^1])
            {
                if (target is JsonArray array) target = array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] ?? throw new InvalidOperationException("The patch coordinate no longer exists.");
                else
                {
                    target[part] ??= new JsonObject(); target = target[part]!;
                }
            }
            if (target is JsonArray list) list[int.Parse(path[^1], System.Globalization.CultureInfo.InvariantCulture)] = response["changes"]![key]?.DeepClone();
            else target[path[^1]] = response["changes"]![key]?.DeepClone();
        }
        return result;
    }

    private static bool Read(JsonNode? root, string[] path, out JsonNode? value)
    {
        value = root;
        foreach (var part in path)
            if (value is JsonObject obj) { if (!obj.TryGetPropertyValue(part, out value)) return false; }
            else if (value is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count) value = array[index];
            else return false;
        return true;
    }
}
