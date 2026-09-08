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

    internal static PlanningUnitPatches Create(PlanningGraph graph, PlanningConstructionUnit unit, JsonObject full, PlanningPreparation? preparation = null)
    {
        if (preparation is not null && unit.Candidate is not null && unit.Kind == "implementation")
        {
            // Diagnostics refer to the candidate's member order, which can differ
            // from the retained graph (or its still-empty construction skeleton).
            try { graph = PlanningConstruction.Apply(graph, unit, unit.Candidate, preparation); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { /* Shape/explicit candidate coordinates still provide bounded repair. */ }
        }
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
                "nodes" => located.First(n => n.Node.Key == slot.Parts[1]).Path + "/" + (slot.Parts[2] is "context" or "conditions" ? "input" : slot.Parts[2] == "arguments" ? ArgumentPath(slot.Parts) : slot.Parts[2]),
                "inputs" => graphRoot + "/inputs/" + workflow.Inputs.FindIndex(p => p.Name == slot.Parts[1]) + "/" + slot.Parts[2],
                _ => graphRoot + "/outputs/" + workflow.Outputs.FindIndex(p => p.Name == slot.Parts[1]) + "/" + slot.Parts[2]
            };
            var diagnosed = Diagnosed(graphPath, slot.Parts);
            if (invalid || diagnosed)
            {
                var leaves = new List<(string[] Path, JsonNode Shape)>();
                if (slot.Parts is ["nodes", _, "onError"] && value is JsonArray handlers)
                    for (var i = 0; i < handlers.Count; i++)
                        foreach (var field in new[] { "if", "setOutput" })
                        {
                            var fieldLocation = graphPath + "/" + i + "/" + field;
                            if (!unit.Diagnostics.Any(d => d.Location == fieldLocation || d.Location.StartsWith(fieldLocation + "/", StringComparison.Ordinal))) continue;
                            var fieldPath = slot.Parts.Concat([i.ToString(System.Globalization.CultureInfo.InvariantCulture), field]).ToArray();
                            var errorDefinition = Definition(slot.Parts, "errorCase");
                            var fieldShape = full["$defs"]![errorDefinition]!["properties"]![field]!;
                            var before = leaves.Count;
                            FindValues(handlers[i]?[field], fieldPath, fieldLocation, fieldShape, leaves);
                            if (leaves.Count == before) leaves.Add((fieldPath, fieldShape));
                        }
                if (unit.Kind is "contracts" or "inputs") FindSchemas(value, slot.Parts, graphPath, slot.Schema, leaves);
                else FindValues(value, slot.Parts, graphPath, slot.Schema, leaves);
                if (leaves.Count == 0) leaves.Add((slot.Parts, slot.Schema));
                foreach (var leaf in leaves)
                {
                    var coordinate = string.Join("/", leaf.Path.Select(PlanningSchemaReferences.Escape));
                    var shape = leaf.Shape.DeepClone();
                    if (preparation is not null && slot.Parts is ["nodes", var nodeKey, "input"] &&
                        unit.Diagnostics.Any(d => d.Code == "LOOP_ITEMS_CONTRACT_UNRESOLVED" &&
                            d.Location == graphPath + "/" + string.Join("/", leaf.Path.Skip(slot.Parts.Length))))
                    {
                        var arrays = PlanningDataflow.CompactIndex(workflow, preparation, graph, nodeKey).Values
                            .Where(b => b.Schema["type"]?.ToString() == "array" && b.Schema["items"] is JsonObject)
                            .Select(b => b.Id).ToArray();
                        if (arrays.Length > 0) shape = PlanningDataflow.BindingSchema(arrays);
                    }
                    selected[coordinate] = leaf.Path; changes[coordinate] = shape;
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

        void FindSchemas(JsonNode? value, string[] path, string location, JsonNode shape, List<(string[] Path, JsonNode Shape)> leaves)
        {
            if (value is not JsonObject obj) return;
            if (unit.Diagnostics.Any(d => d.Code == PlanningProducerRepair.DiagnosticCode && d.Location == location))
            { leaves.Add((path, shape)); return; }
            var before = leaves.Count;
            var definition = path.Contains("structuredOutput", StringComparer.Ordinal) ? "strictSchema" : "schema";
            var schemaShape = new JsonObject { ["$ref"] = "#/$defs/" + definition };
            if (obj["schema"] is JsonObject wrapped) Select(wrapped, ["schema"]);
            if (obj["properties"] is JsonArray properties)
                for (var i = 0; i < properties.Count; i++) Select(properties[i]?["schema"], ["properties", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "schema"]);
            foreach (var field in new[] { "items", "additionalProperties" }) if (obj[field] is JsonObject child) Select(child, [field]);
            if (before == leaves.Count && Diagnosed(location, path)) leaves.Add((path, shape));

            void Select(JsonNode? child, string[] suffix)
            {
                var childPath = path.Concat(suffix).ToArray(); var childLocation = location + "/" + string.Join("/", suffix);
                if (child is null || !Diagnosed(childLocation, childPath)) return;
                FindSchemas(child, childPath, childLocation, schemaShape, leaves);
            }
        }

        void FindValues(JsonNode? value, string[] path, string location, JsonNode shape, List<(string[] Path, JsonNode Shape)> leaves)
        {
            if (value is not JsonObject obj || obj["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var label)) return;
            // A finding on the object itself can require different member names or a
            // computation instead of a context. Leaf-only patches cannot fix its shape.
            var candidateLocation = "/units/" + PlanningSchemaReferences.Escape(unit.Key) + "/candidate/" + string.Join("/", path.Select(PlanningSchemaReferences.Escape));
            if (unit.Diagnostics.Any(d => d.Location == location || d.Location == candidateLocation))
            { leaves.Add((path, shape)); return; }
            var before = leaves.Count;
            if (label is "object" or "template" && obj["members"] is JsonArray members)
                for (var i = 0; i < members.Count; i++) SelectChild(members[i]?["value"], path.Concat(["members", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "value"]).ToArray(), location + "/members/" + i + "/value");
            if (label == "array" && obj["items"] is JsonArray items)
                for (var i = 0; i < items.Count; i++) SelectChild(items[i], path.Concat(["items", i.ToString(System.Globalization.CultureInfo.InvariantCulture)]).ToArray(), location + "/items/" + i);
            if (before == leaves.Count && Diagnosed(location, path)) leaves.Add((path, shape));

            void SelectChild(JsonNode? child, string[] childPath, string childLocation)
            {
                var referencePath = "#/$defs/" + Definition(path, "value");
                var childSchema = new JsonObject { ["$ref"] = referencePath, ["$defs"] = full["$defs"]!.DeepClone() };
                if (PlanningContractValidation.ValidateInstance(child, childSchema).Count == 0 && !Diagnosed(childLocation, childPath)) return;
                var count = leaves.Count;
                var reference = new JsonObject { ["$ref"] = referencePath };
                FindValues(child, childPath, childLocation, reference, leaves);
                if (leaves.Count == count) leaves.Add((childPath, reference));
            }
        }
        bool Diagnosed(string location, string[] path)
        {
            var candidateRoot = "/units/" + PlanningSchemaReferences.Escape(unit.Key) + "/candidate/";
            var candidateLocation = candidateRoot + string.Join("/", path.Select(PlanningSchemaReferences.Escape));
            return unit.Diagnostics.Any(d => Matches(location, d.Location) || d.Location.StartsWith(candidateRoot, StringComparison.Ordinal) && Matches(candidateLocation, d.Location));
            static bool Matches(string target, string diagnostic) => diagnostic == target || diagnostic.StartsWith(target + "/", StringComparison.Ordinal) || target.StartsWith(diagnostic + "/", StringComparison.Ordinal);
        }
        string Definition(string[] path, string name) => path is ["nodes", var node, ..] && full["$defs"]![PlanningConstruction.ValueScope(node) + name] is not null
            ? PlanningConstruction.ValueScope(node) + name : name;
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
