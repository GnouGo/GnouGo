using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Model contracts expose unresolved fields and compiler-owned binding choices only.</summary>
internal static class PlanningHoleRequests
{
    internal sealed record Request(string Prompt, JsonObject Schema, Dictionary<string, PlanningValue> Bindings, JsonNode Context)
    { internal Dictionary<string, List<string>> ParameterScopes { get; init; } = new(StringComparer.Ordinal); }

    internal static Request Create(PlanningSnapshot state, PlanningWorkflow workflow, IReadOnlyList<PlanningHole> holes)
    {
        var definitions = PlanningSchemas.ValueDefinitions().DeepClone().AsObject();
        foreach (var name in definitions.Select(p => p.Key).Where(k => k is not ("schema" or "port" or "value" or "member")).ToArray()) definitions.Remove(name);
        definitions["computationText"] = PlanningComputationScopes.ExpressionSchema();
        var literalVariants = definitions["value"]!["anyOf"]!.AsArray();
        foreach (var variant in literalVariants.ToArray())
            if (variant?["properties"]?["kind"]?["enum"]?[0]?.ToString() is not ("null" or "string" or "number" or "boolean" or "object" or "array")) literalVariants.Remove(variant);
        // Literal strings are data; the expression variant shares the old string shape.
        literalVariants.Single(v => v!["properties"]!["kind"]!["enum"]![0]!.ToString() == "string")!["properties"]!["kind"]!["enum"] = new JsonArray("string");
        // Runtime defaults are applied at workflow input boundaries, not to result
        // contracts. Result-schema choices must not invent unavailable values, and
        // need no recursive literal/default authoring grammar in their response.
        if (!holes.Any(h => h.Kind == "schema" && h.NodeKey is null && h.Path.Contains("/inputs/", StringComparison.Ordinal)))
            definitions["port"]!["properties"]!["default"] = Type("null");
        var relevant = PlanningWorkflowConstruction.RelevantPreparation(state.Preparation!, workflow);
        var capabilities = holes.Select(h => PlanningHoleContracts.Target(workflow, h).Node?.CapabilityId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        relevant.Capabilities = relevant.Capabilities.Where(c => capabilities.Contains(c.Id)).ToList();
        var references = PlanningSchemaReferences.Index(relevant).Where(r => PlanningSchemaPropagation.Established(PlanningSchemaReferences.Resolve(
            new() { CapabilityId = r!["capabilityId"]!.ToString(), SchemaPointer = r["schemaPointer"]!.ToString() }, relevant))).ToArray();
        var schemaVariants = definitions["schema"]!["anyOf"]!.AsArray(); schemaVariants.RemoveAt(0);
        foreach (var group in references.GroupBy(r => r!["capabilityId"]!.ToString(), StringComparer.Ordinal))
            schemaVariants.Add((JsonNode)Object(("kind", Enum("reference")), ("capabilityId", Enum(group.Key)), ("schemaPointer", Enum(group.Select(r => r!["schemaPointer"]!.ToString()).ToArray()))));
        foreach (var inline in schemaVariants.OfType<JsonObject>().Where(v => v["properties"]?["kind"]?["enum"]?[0]?.ToString() == "inline").ToArray())
        {
            schemaVariants.Remove(inline);
            foreach (var type in new[] { "string", "number", "integer", "boolean", "array", "object" })
            {
                var variant = inline.DeepClone().AsObject(); var properties = variant["properties"]!;
                properties["type"] = Enum(type);
                properties["items"] = type == "array" ? new JsonObject { ["$ref"] = "#/$defs/schema" } : Type("null");
                if (type != "object") { properties["properties"]!["maxItems"] = 0; properties["additionalProperties"] = Type("null"); }
                if (type != "string") properties["enum"]!["maxItems"] = 0;
                schemaVariants.Add((JsonNode)variant);
            }
        }
        var objectSchemas = new JsonArray();
        foreach (var variant in schemaVariants.OfType<JsonObject>())
        {
            if (variant["properties"]!["kind"]!["enum"]![0]!.ToString() == "inline")
            {
                if (variant["properties"]!["type"]!["enum"]![0]!.ToString() != "object") continue;
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
        if (holes.Any(h => h.Path.EndsWith("/structuredOutput/schema", StringComparison.Ordinal)))
        {
            foreach (var name in new[] { "schema", "objectSchema", "port" })
            {
                var strict = definitions[name]!.DeepClone();
                Rewrite(strict);
                if (name == "port")
                { strict["properties"]!["required"] = new JsonObject { ["type"] = "boolean", ["const"] = true }; strict["properties"]!["default"] = Type("null"); }
                definitions["strict_" + name] = strict;
            }
            void Rewrite(JsonNode? current)
            {
                if (current is JsonObject obj)
                {
                    if (obj["$ref"]?.ToString() is "#/$defs/schema" or "#/$defs/port") obj["$ref"] = "#/$defs/strict_" + obj["$ref"]!.ToString()[8..];
                    if (obj["properties"]?["kind"]?["enum"]?[0]?.ToString() == "inline") obj["properties"]!["additionalProperties"] = Type("null");
                    if (obj["properties"]?["kind"]?["enum"]?[0]?.ToString() == "reference")
                    {
                        var properties = obj["properties"]!;
                        var capability = properties["capabilityId"]!["enum"]![0]!.ToString();
                        var pointers = properties["schemaPointer"]!["enum"]!.AsArray();
                        foreach (var pointer in pointers.ToArray())
                        {
                            var referenced = PlanningSchemaReferences.Resolve(new() { CapabilityId = capability, SchemaPointer = pointer!.ToString() }, relevant);
                            if (PlanningContractValidation.ValidateSchema(Object(("field", referenced)), true).Count > 0) pointers.Remove(pointer);
                        }
                    }
                    foreach (var child in obj.ToArray()) Rewrite(child.Value);
                    if (obj["anyOf"] is JsonArray variants)
                        foreach (var variant in variants.OfType<JsonObject>().Where(v => v["properties"]?["schemaPointer"]?["enum"] is JsonArray { Count: 0 }).ToArray()) variants.Remove(variant);
                }
                else if (current is JsonArray array) foreach (var child in array) Rewrite(child);
            }
        }
        var fields = new JsonObject(); var descriptions = new JsonObject(); var bindingContext = new JsonObject();
        var bindings = new Dictionary<string, PlanningValue>(StringComparer.Ordinal);
        var parameterScopes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var hole in holes)
        {
            var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SingleOrDefault(n => n.Key == hole.NodeKey);
            if (hole.Kind == "schema" && hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal) && node?.Type == "mcp.call" &&
                state.Preparation!.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId) is { } external && !PlanningSchemaPropagation.Established(external.OutputSchema))
                throw new PlanningHoleUnavailableException(hole.CanonicalLocation, "The external result has no authoritative typed contract. Supply provider metadata or clarify the boundary; a model cannot establish an opaque result schema.");
            if (node?.Type == "switch" && PlanningDecisionRouting.Contract(node, state.Preparation!) is not null)
                throw new InvalidOperationException("The locked decision producer is not available. Establish its contract and provenance before routing.");
            var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
            hole.DirectCandidateCount = domain.Direct.Count;
            hole.ComputationParameterCount = domain.Parameters.Count;
            var available = domain.Direct.Concat(domain.Parameters).DistinctBy(b => b.Id).ToArray();
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
                if (domain.Direct.Any(b => b.Id == binding.Id)) choices.Add((JsonNode)JsonValue.Create(parameter)!);
            }
            var variants = new JsonArray();
            if (hole.Kind == "schema") fields[hole.Id] = new JsonObject { ["$ref"] = hole.Path.EndsWith("/structuredOutput/schema", StringComparison.Ordinal) ? "#/$defs/strict_objectSchema" : node?.Type == "set" && hole.Path.EndsWith("/outputSchema", StringComparison.Ordinal) ? "#/$defs/objectSchema" : "#/$defs/schema" };
            else
            {
                if (choices.Count > 0)
                {
                    variants.Add((JsonNode)Object(("kind", Enum("binding")), ("binding", new JsonObject { ["type"] = "string", ["enum"] = choices.DeepClone() })));
                }
                if (domain.Parameters.Count > 0)
                {
                    var parameters = new JsonArray(domain.Parameters.Select(b => (JsonNode?)JsonValue.Create("p_" + b.Id[2..14])).ToArray());
                    parameterScopes[hole.Id] = parameters.Select(p => p!.ToString()).ToList();
                    variants.Add((JsonNode)Object(("kind", Enum("compute")), ("expression", new JsonObject { ["$ref"] = "#/$defs/computationText" })));
                }
                if (domain.Literal)
                    variants.Add((JsonNode)Object(("kind", Enum("literal")), ("json", PlanningLiteralSchemas.Create(domain.Contract, hole.CanonicalLocation))));
                if (hole.Kind == "default" || domain.Omission) variants.Add((JsonNode)Object(("kind", Enum("absent"))));
                if (variants.Count == 0) throw new PlanningHoleUnavailableException(hole.CanonicalLocation, "No binding or computation satisfies the field's available contracts and outstanding obligations.");
                fields[hole.Id] = new JsonObject { ["anyOf"] = variants };
            }
            descriptions[hole.Id] = new JsonObject { ["field"] = hole.CanonicalLocation.Length > 0 ? hole.CanonicalLocation : hole.Path, ["executor"] = node?.Type, ["obligation"] = hole.Purpose, ["parameters"] = parameterScopes.TryGetValue(hole.Id, out var scope) ? new JsonArray(scope.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) : null, ["expected"] = hole.Kind == "schema" || domain.Parameters.Count > 0 ? domain.Contract?.DeepClone() : null };
        }
        var assignments = new JsonObject { ["type"] = "object", ["properties"] = fields, ["required"] = new JsonArray(holes.Select(h => (JsonNode?)JsonValue.Create(h.Id)).ToArray()), ["additionalProperties"] = false };
        var schema = Object(("assignments", assignments)); schema["$defs"] = definitions;
        PruneDefinitions(schema);
        var context = new JsonObject { ["holes"] = descriptions, ["bindings"] = bindingContext };
        if (holes.Any(h => h.Kind == "schema"))
        {
            if (BoundaryEvidence(state, workflow) is { Count: > 0 } evidence) context["boundaryEvidence"] = evidence;
            var selected = holes.Select(h => PlanningHoleContracts.Target(workflow, h).Node?.CapabilityId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            context["contracts"] = new JsonObject(relevant.Capabilities.Where(c => selected.Contains(c.Id)).Select(c => new KeyValuePair<string, JsonNode?>(c.Id,
                new JsonObject { ["output"] = c.OutputSchema.DeepClone() })));
            var sources = new JsonObject();
            foreach (var hole in holes.Where(h => h.Kind == "schema"))
            {
                if (hole.NodeKey is null && hole.Path.Contains("/inputs/", StringComparison.Ordinal)) continue;
                var node = PlanningHoleContracts.Target(workflow, hole).Node;
                var obligations = PlanningHoleEligibility.Obligations(state, workflow, node);
                var included = new List<PlanningBinding>();
                foreach (var binding in PlanningDataflow.Index(workflow, state.Preparation!, state.Graph!, node?.Key ?? PlanningDataflow.WorkflowOutputs).Values
                    .OrderBy(b => b.Value.Path.Count).ThenBy(b => b.Id, StringComparer.Ordinal))
                {
                    if (!PlanningSchemaPropagation.Established(binding.Schema) || binding.Availability is "opaque" or "absent" ||
                        node is not null && !PlanningHoleEligibility.Dependencies(state, workflow, node, binding.Value).Overlaps(obligations)) continue;
                    // A typed parent's contract already contains its descendant
                    // contracts. Keep the smallest available typed roots, including
                    // a branch projection when its containing envelope is opaque.
                    if (included.Any(parent => parent.Value.Kind == binding.Value.Kind && parent.Value.Source == binding.Value.Source && parent.Value.ResultChannel == binding.Value.ResultChannel &&
                        parent.Value.Path.Count <= binding.Value.Path.Count && parent.Value.Path.SequenceEqual(binding.Value.Path.Take(parent.Value.Path.Count), StringComparer.Ordinal))) continue;
                    included.Add(binding);
                    sources[binding.Id] = new JsonObject { ["source"] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(binding.Value, PlanningJsonContext.Default.PlanningValue)), ["schema"] = binding.Schema.DeepClone() };
                }
            }
            if (sources.Count > 0) context["sourceContracts"] = sources;
            var consumers = new JsonObject();
            var producerOperations = holes.Where(h => h.Kind == "schema").Select(h => PlanningHoleContracts.Target(workflow, h).Node)
                .OfType<PlanningNode>().SelectMany(n => n.OperationIds).ToHashSet(StringComparer.Ordinal);
            foreach (var consumer in state.Preparation!.Capabilities.Where(c => c.InputOperationIds.Any(producerOperations.Contains)))
            {
                var unresolved = new JsonObject();
                foreach (var target in state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved && !h.Superseded && h.Kind == "value"))
                    if (PlanningHoleContracts.Target(workflow, target).Node?.CapabilityId == consumer.Id &&
                        !PlanningHoleEligibility.ArtifactKinds(state, workflow, target).Any() && Expected(state, workflow, target) is { } contract)
                        unresolved[target.CanonicalLocation] = contract.DeepClone();
                if (unresolved.Count > 0) consumers[consumer.Id] = new JsonObject { ["obligation"] = consumer.Description, ["unresolvedInputs"] = unresolved };
            }
            if (consumers.Count > 0) context["consumerContracts"] = consumers;
        }
        return new("Assign the unresolved fields. Expressions use the supplied parameters; the coordinator retains only referenced parameters. " +
            PlanningPromptContext.Instructions + "\n" + PlanningPromptContext.Json(PlanningPromptContext.Share(context)), schema, bindings, context) { ParameterScopes = parameterScopes };
    }

    private static JsonObject BoundaryEvidence(PlanningSnapshot state, PlanningWorkflow workflow)
    {
        if (state.BehaviorPlan is null) return new();
        // Related unresolved boundaries still have accepted business contracts. Follow
        // locked operation edges, never descriptions or names, to retain that evidence.
        var operations = workflow.OperationIds.ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var capability in state.Preparation!.Capabilities.Where(c => c.OperationIds.Any(operations.Contains)))
                foreach (var input in capability.InputOperationIds) changed |= operations.Add(input);
        } while (changed);
        var related = state.BehaviorPlan.Workflows.Where(w => w.Key != workflow.Key && w.OperationIds.Any(operations.Contains));
        return new JsonObject(related.Select(w => new KeyValuePair<string, JsonNode?>(w.Key, new JsonObject
        {
            ["inputs"] = new JsonObject(w.Inputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, JsonValue.Create(p.Description)))),
            ["outputs"] = new JsonObject(w.Outputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, JsonValue.Create(p.Description))))
        })));
    }

    internal static IReadOnlyList<PlanningBinding> Catalog(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        return PlanningHoleEligibility.Analyze(state, workflow, hole).Direct;
    }
    internal static JsonObject? Expected(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole) => PlanningHoleContracts.Expected(state, workflow, hole);
    internal static string Scope(IEnumerable<PlanningHole> holes, JsonObject schema) => PlanningGraphCompiler.Fingerprint(
        string.Join("\n", holes.OrderBy(h => h.Id, StringComparer.Ordinal).Select(h => h.Id + "|" + h.CanonicalLocation + "|" + h.Kind)) + "\n" + schema.ToJsonString());
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
