using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Model contracts expose unresolved fields and compiler-owned binding choices only.</summary>
internal static class PlanningHoleRequests
{
    internal sealed record Request(string Prompt, JsonObject Schema, Dictionary<string, PlanningValue> Bindings, JsonNode Context);

    internal static Request Create(PlanningSnapshot state, PlanningWorkflow workflow, IReadOnlyList<PlanningHole> holes)
    {
        var definitions = PlanningSchemas.ValueDefinitions().DeepClone().AsObject();
        foreach (var name in definitions.Select(p => p.Key).Where(k => k is not ("schema" or "port" or "value" or "member")).ToArray()) definitions.Remove(name);
        var literalVariants = definitions["value"]!["anyOf"]!.AsArray();
        foreach (var variant in literalVariants.ToArray())
            if (variant?["properties"]?["kind"]?["enum"]?[0]?.ToString() is not ("null" or "string" or "number" or "boolean" or "object" or "array")) literalVariants.Remove(variant);
        // Literal strings are data; the expression variant shares the old string shape.
        literalVariants.Single(v => v!["properties"]!["kind"]!["enum"]![0]!.ToString() == "string")!["properties"]!["kind"]!["enum"] = new JsonArray("string");
        var relevant = PlanningWorkflowConstruction.RelevantPreparation(state.Preparation!, workflow);
        var references = PlanningSchemaReferences.Index(relevant);
        var schemaVariants = definitions["schema"]!["anyOf"]!.AsArray(); schemaVariants.RemoveAt(0);
        foreach (var group in references.GroupBy(r => r!["capabilityId"]!.ToString(), StringComparer.Ordinal))
            schemaVariants.Add((JsonNode)Object(("kind", Enum("reference")), ("capabilityId", Enum(group.Key)), ("schemaPointer", Enum(group.Select(r => r!["schemaPointer"]!.ToString()).ToArray()))));
        var objectSchemas = new JsonArray();
        foreach (var variant in schemaVariants.OfType<JsonObject>())
        {
            if (variant["properties"]!["kind"]!["enum"]![0]!.ToString() == "inline")
            {
                var objectSchema = variant.DeepClone().AsObject(); objectSchema["properties"]!["type"] = Enum("object");
                objectSchemas.Add((JsonNode)objectSchema);
            }
            else
            {
                var copy = variant.DeepClone().AsObject(); var properties = copy["properties"]!;
                var capability = relevant.Capabilities.Single(c => c.Id == properties["capabilityId"]!["enum"]![0]!.ToString());
                var pointers = properties["schemaPointer"]!["enum"]!.AsArray().Select(p => p!.ToString()).Where(pointer =>
                    PlanningSchemaReferences.Resolve(new() { CapabilityId = capability.Id, SchemaPointer = pointer }, relevant)["type"]?.ToString() == "object").ToArray();
                if (pointers.Length == 0) continue;
                properties["schemaPointer"] = Enum(pointers); objectSchemas.Add((JsonNode)copy);
            }
        }
        definitions["objectSchema"] = new JsonObject { ["anyOf"] = objectSchemas };
        var fields = new JsonObject(); var descriptions = new JsonObject(); var bindingContext = new JsonObject();
        var bindings = new Dictionary<string, PlanningValue>(StringComparer.Ordinal);
        foreach (var hole in holes)
        {
            var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SingleOrDefault(n => n.Key == hole.NodeKey);
            if (node?.Type == "switch" && PlanningDecisionRouting.Contract(node, state.Preparation!) is not null)
                throw new InvalidOperationException("The locked decision producer is not available. Establish its contract and provenance before routing.");
            var available = Catalog(state, workflow, hole);
            var choices = new JsonArray();
            foreach (var binding in available)
            {
                var parameter = "p_" + binding.Id[2..14];
                if (bindings.TryGetValue(parameter, out var existing) && PlanningBindingIdentity.Id(existing) != binding.Id) throw new InvalidOperationException("Binding parameter identity collision.");
                bindings[parameter] = binding.Value;
                if (!bindingContext.ContainsKey(parameter)) bindingContext[parameter] = new JsonObject
                {
                    ["value"] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(binding.Value, PlanningJsonContext.Default.PlanningValue)),
                    ["schema"] = binding.Schema.DeepClone(), ["availability"] = binding.Availability
                };
                choices.Add((JsonNode)JsonValue.Create(parameter)!);
            }
            var variants = new JsonArray();
            if (hole.Kind == "schema") fields[hole.Id] = new JsonObject { ["$ref"] = node?.Type == "set" && hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal) ? "#/$defs/objectSchema" : "#/$defs/schema" };
            else
            {
                if (choices.Count > 0)
                {
                    variants.Add((JsonNode)Object(("kind", Enum("binding")), ("binding", new JsonObject { ["type"] = "string", ["enum"] = choices.DeepClone() })));
                    variants.Add((JsonNode)Object(("kind", Enum("compute")), ("expression", Type("string")),
                        ("bindings", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = choices.DeepClone() }, ["maxItems"] = choices.Count })));
                }
                variants.Add((JsonNode)Object(("kind", Enum("literal")), ("value", new JsonObject { ["$ref"] = "#/$defs/value" })));
                if (hole.Kind == "default") variants.Add((JsonNode)Object(("kind", Enum("absent"))));
                fields[hole.Id] = new JsonObject { ["anyOf"] = variants };
            }
            descriptions[hole.Id] = new JsonObject { ["field"] = hole.CanonicalLocation.Length > 0 ? hole.CanonicalLocation : hole.Path, ["executor"] = node?.Type, ["obligation"] = hole.Purpose, ["expected"] = Expected(state, workflow, hole)?.DeepClone() };
        }
        var assignments = new JsonObject { ["type"] = "object", ["properties"] = fields, ["required"] = new JsonArray(holes.Select(h => (JsonNode?)JsonValue.Create(h.Id)).ToArray()), ["additionalProperties"] = false };
        var schema = Object(("assignments", assignments)); schema["$defs"] = definitions;
        PruneDefinitions(schema);
        var context = new JsonObject { ["holes"] = descriptions, ["bindings"] = bindingContext };
        if (holes.Any(h => h.Kind == "schema"))
            context["contracts"] = new JsonObject(relevant.Capabilities.Select(c => new KeyValuePair<string, JsonNode?>(c.Id,
                new JsonObject { ["input"] = c.InputSchema.DeepClone(), ["output"] = c.OutputSchema.DeepClone() })));
        return new("Fill the unresolved fields. Control flow and workflow calls are already fixed. A loop items field supplies its source collection; its body performs iteration and calls. A compute expression uses only its selected parameter names from bindings. " +
            "Literal values are data; schemas must preserve the stated contracts. Bindings and result projections are supplied by the coordinator. " +
            PlanningPromptContext.Instructions + "\n" + PlanningPromptContext.Share(context).ToJsonString(), schema, bindings, context);
    }

    internal static IReadOnlyList<PlanningBinding> Catalog(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        if (hole.Kind is "schema" or "default") return [];
        return PlanningDataflow.Index(workflow, state.Preparation!, state.Graph!, hole.NodeKey ?? PlanningDataflow.WorkflowOutputs).Values
            .Where(b => b.Availability != "opaque").OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
    }
    internal static JsonObject? Expected(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        if (hole.ExpectedSchema is not null) return hole.ExpectedSchema;
        try
        {
            if (hole.NodeKey is null)
            {
                var root = "/workflows/" + state.Graph!.Workflows.IndexOf(workflow);
                var parts = hole.Path[(root.Length + 1)..].Split('/');
                var contract = parts[0] == "outputs" ? workflow.Outputs[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)].Schema : workflow.Inputs[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)].Schema;
                return PlanningGraphCompiler.ToJsonSchema(contract, state.Preparation!);
            }
            var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Single(n => n.Key == hole.NodeKey);
            if (node.Type == "workflow.call")
            {
                var arguments = node.Input.Members.Single(m => m.Name == "args").Value;
                var index = int.Parse(hole.Path.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);
                var callee = state.Graph!.Workflows.Single(w => w.Key == PlanningWorkflowProvenance.Target(node));
                return PlanningGraphCompiler.ToJsonSchema(callee.Inputs.Single(p => p.Name == arguments.Members[index].Name).Schema, state.Preparation!);
            }
            return node.OutputSchema is null ? null : PlanningGraphCompiler.ToJsonSchema(node.OutputSchema, state.Preparation!);
        }
        catch (InvalidOperationException) { return null; }
    }
    internal static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false
    };
    internal static JsonObject Type(string type) => new() { ["type"] = type };
    internal static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    internal static void PruneDefinitions(JsonObject schema)
    {
        if (schema["$defs"] is not JsonObject definitions) return;
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference) && reference.StartsWith("#/$defs/", StringComparison.Ordinal) && used.Add(reference[8..])) Visit(definitions[reference[8..]]);
                foreach (var pair in obj.Where(p => p.Key != "$defs")) Visit(pair.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(schema);
        foreach (var name in definitions.Select(p => p.Key).Where(k => !used.Contains(k)).ToArray()) definitions.Remove(name);
        if (definitions.Count == 0) schema.Remove("$defs");
    }
}
