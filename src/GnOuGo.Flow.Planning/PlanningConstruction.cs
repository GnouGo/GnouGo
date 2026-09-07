using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Construction transport never owns topology. Every editable slot is declared by the host.</summary>
public static class PlanningConstruction
{
    private static JsonObject Object(JsonObject properties) => new()
    {
        ["type"] = "object", ["properties"] = properties,
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()), ["additionalProperties"] = false
    };
    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/$defs/" + name };
    private static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
    private static JsonNode Compact<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => PlanningModelValues.Compact(JsonSerializer.SerializeToNode(value, type))!;

    public static List<PlanningConstructionUnit> Partition(PlanningWorkflow workflow, int size)
    {
        if (size is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(size));
        var result = new List<PlanningConstructionUnit>();
        PlanningConstructionUnit Add(string kind, IEnumerable<string> keys, IEnumerable<string> dependencies)
        {
            var names = keys.ToList();
            var unit = new PlanningConstructionUnit { WorkflowKey = workflow.Key, Kind = kind, NodeKeys = names, ContractVersion = PlanningDataflow.ContractVersion,
                Key = workflow.Key + ":" + kind + ":" + PlanningGraphCompiler.Fingerprint(string.Join("\n", names))[..16], Dependencies = dependencies.ToList() };
            result.Add(unit); return unit;
        }
        var inputs = Add("inputs", [], []);
        var groups = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Chunk(size).ToArray();
        var contracts = groups.Select(nodes => Add("contracts", nodes.Select(n => n.Key), [inputs.Key])).ToArray();
        // Conservative execution-order dependencies also cover implicit expression references.
        // Independent contract work and separate workflows can execute concurrently.
        string? previous = null;
        var implementations = new List<PlanningNode[]>(); var pending = new List<PlanningNode>();
        foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)))
        {
            if (node.Type is "loop.sequential" or "loop.parallel")
            {
                if (pending.Count > 0) { implementations.Add(pending.ToArray()); pending.Clear(); }
                implementations.Add([node]);
            }
            else { pending.Add(node); if (pending.Count == size) { implementations.Add(pending.ToArray()); pending.Clear(); } }
        }
        if (pending.Count > 0) implementations.Add(pending.ToArray());
        foreach (var nodes in implementations)
        {
            var unit = Add("implementation", nodes.Select(n => n.Key), contracts.Select(c => c.Key).Concat(previous is null ? [] : new[] { previous }));
            previous = unit.Key;
        }
        Add("outputs", [], previous is null ? [inputs.Key] : [previous]);
        return result;
    }

    public static JsonObject Schema(PlanningWorkflow workflow, PlanningConstructionUnit unit, PlanningPreparation preparation, PlanningGraph? graph = null)
    {
        var definitions = PlanningSchemas.Graph(preparation)["$defs"]!.DeepClone().AsObject();
        var values = definitions["value"]!["anyOf"]!.AsArray();
        foreach (var variant in values.OfType<JsonObject>().ToArray())
        {
            var kinds = variant["properties"]?["kind"]?["enum"] as JsonArray;
            var kind = kinds?.FirstOrDefault()?.GetValue<string>();
            if (kind is not ("input" or "output" or "workflow")) continue;
            var names = kind == "input" ? workflow.Inputs.Select(p => p.Name).ToArray()
                : kind == "workflow" ? (graph?.Workflows.Select(w => w.Key).ToArray() ?? [workflow.Key])
                : PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Select(n => n.Key).ToArray();
            if (names.Length == 0) values.Remove(variant);
            else variant["properties"]!["source"]!["enum"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        }
        if (unit.ContractVersion >= PlanningDataflow.BindingVersion && unit.Kind == "implementation" && graph is not null)
        {
            foreach (var variant in values.OfType<JsonObject>().ToArray())
                if (variant["properties"]?["kind"]?["enum"] is JsonArray kinds && kinds.Any(k => k?.ToString() is "input" or "output")) values.Remove(variant);
                else if (variant["properties"]?["kind"]?["enum"] is JsonArray literalKinds && literalKinds.Any(k => k?.ToString() == "expression"))
                    variant["properties"]!["kind"]!["enum"] = new JsonArray("string");
            values.Add((JsonNode)Object(new() { ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("compute") },
                ["text"] = new JsonObject { ["type"] = "string", ["description"] = "Executable JavaScript using named members as parameters. Return an expression such as value.trim(), or a function body ending with return. Never prose or pseudocode." },
                ["members"] = new JsonObject { ["type"] = "array", ["items"] = Ref("member") } }));
            var bindings = unit.NodeKeys.SelectMany(key => PlanningDataflow.CompactIndex(workflow, preparation, graph, key)).DistinctBy(p => p.Key).ToArray();
            if (bindings.Length != 0) values.Add((JsonNode)PlanningDataflow.BindingSchema(bindings.Select(p => p.Key)));
        }
        // Exact reference pairs prevent data paths and unsupported boundary references.
        var references = PlanningSchemaReferences.Index(preparation).OfType<JsonObject>().Where(entry =>
        {
            try
            {
                var declared = new PlanningSchema { CapabilityId = entry["capabilityId"]!.GetValue<string>(), SchemaPointer = entry["schemaPointer"]!.GetValue<string>() };
                var json = PlanningGraphCompiler.ToJsonSchema(declared, preparation);
                PlanningGraphValidation.RequireTyped(json, 0);
                if (unit.Kind is "inputs" or "outputs") PlanningGraphCompiler.ToFlowSchema(json);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }).GroupBy(entry => entry["capabilityId"]!.GetValue<string>(), StringComparer.Ordinal).Select(group => (JsonNode)Object(new()
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("reference") },
            ["capabilityId"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(group.Key) },
            ["schemaPointer"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(group.Select(entry => entry["schemaPointer"]!.DeepClone()).ToArray()) }
        })).ToArray();
        var schemaVariants = definitions["schema"]!["anyOf"]!.AsArray();
        var inline = schemaVariants[1]!.DeepClone();
        definitions["schema"] = new JsonObject { ["anyOf"] = new JsonArray(new[] { inline }.Concat(references).ToArray()) };
        // Structured post-processing has a stricter destination contract than a
        // native producer annotation. Do not offer references that conversion would reject.
        var strictInline = inline.DeepClone(); var strictFields = strictInline["properties"]!;
        strictFields["items"] = Nullable(Ref("strictSchema"));
        strictFields["properties"]!["items"] = Ref("strictPort");
        strictFields["additionalProperties"] = new JsonObject { ["type"] = "null" };
        var strictReferences = new List<JsonNode>();
        foreach (var reference in references)
        {
            var copy = reference.DeepClone(); var fields = copy["properties"]!;
            var capability = fields["capabilityId"]!["enum"]![0]!.GetValue<string>();
            var pointers = fields["schemaPointer"]!["enum"]!.AsArray();
            foreach (var pointer in pointers.ToArray())
                if (PlanningContractValidation.ValidateSchema(PlanningGraphCompiler.ToJsonSchema(new() { CapabilityId = capability, SchemaPointer = pointer!.GetValue<string>() }, preparation), strict: true).Count != 0) pointers.Remove(pointer);
            if (pointers.Count > 0) strictReferences.Add(copy);
        }
        definitions["strictSchema"] = new JsonObject { ["anyOf"] = new JsonArray(new[] { strictInline }.Concat(strictReferences).ToArray()) };
        definitions["strictPort"] = definitions["port"]!.DeepClone(); definitions["strictPort"]!["properties"]!["schema"] = Ref("strictSchema");
        var root = new JsonObject();
        if (unit.Kind == "inputs") root["inputs"] = Object(new JsonObject(workflow.Inputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name,
            Object(new() { ["schema"] = Ref("schema"), ["default"] = Nullable(Ref("value")) })))));
        else if (unit.Kind == "outputs")
        {
            var bindings = PlanningOutputBindings.Index(workflow, preparation, graph);
            if (workflow.Outputs.Count > 0 && bindings.Count == 0) throw new InvalidOperationException("No producer has an exportable public contract. Establish a validated transformation before exporting outputs.");
            definitions["outputReference"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(bindings.Keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()) };
            root["outputs"] = Object(new JsonObject(workflow.Outputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name,
                Object(new() { ["reference"] = Ref("outputReference") })))));
        }
        else
        {
            var nodes = new JsonObject();
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => unit.NodeKeys.Contains(n.Key, StringComparer.Ordinal)))
            {
                var fields = new JsonObject();
                if (unit.Kind == "contracts")
                {
                    if (node.Type == "set") fields["outputSchema"] = Ref("schema");
                    if (node.Type is "mcp.call" or "llm.call") fields["structuredOutput"] = PlanningProducerContracts.RequiresStructuredResult(node, preparation)
                        ? Object(new() { ["schema"] = Ref("strictSchema") }) : Nullable(Object(new() { ["schema"] = Ref("strictSchema") }));
                }
                else
                {
                    if (node.Type == "human.input") fields["context"] = Nullable(Ref("value"));
                    else if (PlanningDecisionRouting.LocalContracts(node, preparation).Length > 0) fields["conditions"] = PlanningDecisionRouting.ConditionsSchema(node, preparation);
                    else if (unit.ContractVersion >= PlanningDataflow.BindingVersion && node.Type == "mcp.call" && preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId) is { InputSchema: var inputSchema } capability && inputSchema["properties"] is JsonObject properties)
                    {
                        var required = (inputSchema["required"] as JsonArray ?? []).Select(p => p?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                        fields["arguments"] = Object(new JsonObject(properties.Where(p => !capability.RequestBindings.Any(b => b.Path == "/" + PlanningSchemaReferences.Escape(p.Key))).Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                            (graph is null ? null : PlanningArtifactBindings.ArgumentSchema(workflow, node, p.Key, preparation, graph)) ??
                            (required.Contains(p.Key) ? Ref("value") : new JsonObject { ["anyOf"] = new JsonArray(Ref("value"), Object(new() { ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("omit") } })) })))));
                    }
                    else if (node.Type is not ("sequence" or "parallel" or "switch")) fields["input"] = Ref("value");
                    if (node.Type == "switch" && PlanningDecisionRouting.Contract(node, preparation) is null) fields["expr"] = Ref("value");
                    if (unit.ContractVersion < 11 && node.Type is "loop.sequential" or "loop.parallel")
                        foreach (var key in new[] { "itemVar", "indexVar" }) fields[key] = Nullable(new() { ["type"] = "string" });
                    if (node.Type is not ("sequence" or "parallel" or "switch" or "human.input") && PlanningDecisionRouting.LocalContracts(node, preparation).Length == 0)
                        fields["onError"] = new JsonObject { ["type"] = "array", ["items"] = Ref("errorCase") };
                }
                if (unit.Kind == "implementation" && unit.ContractVersion >= PlanningDataflow.BindingVersion && graph is not null)
                {
                    var prefix = ValueScope(node.Key);
                    var available = PlanningDataflow.CompactIndex(workflow, preparation, graph, node.Key).Keys.ToArray();
                    foreach (var name in new[] { "value", "member", "errorCase" })
                    {
                        var definition = definitions[name]!.DeepClone();
                        if (name == "value")
                        {
                            var options = definition["anyOf"]!.AsArray();
                            if (node.Type != "workflow.call")
                                foreach (var option in options.OfType<JsonObject>().Where(v => v["properties"]?["kind"]?["enum"]?[0]?.ToString() == "workflow").ToArray()) options.Remove(option);
                            foreach (var option in options.OfType<JsonObject>().Where(v => v["properties"]?["kind"]?["enum"]?[0]?.ToString() == "binding").ToArray()) options.Remove(option);
                            if (available.Length > 0) options.Add((JsonNode)PlanningDataflow.BindingSchema(available));
                        }
                        ScopeReferences(definition, prefix); definitions[prefix + name] = definition;
                    }
                    ScopeReferences(fields, prefix);
                }
                nodes[node.Key] = Object(fields);
            }
            root["nodes"] = Object(nodes);
            if (unit.Kind == "implementation") root["functions"] = Nullable(new() { ["type"] = "string" });
        }
        var schema = Object(root); schema["$defs"] = definitions; PruneDefinitions(schema); return schema;
    }

    internal static string ValueScope(string nodeKey) => "scope_" + PlanningGraphCompiler.Fingerprint(nodeKey)[..8] + "_";
    private static void ScopeReferences(JsonNode? node, string prefix)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"]?.ToString() is "#/$defs/value" or "#/$defs/member" or "#/$defs/errorCase") obj["$ref"] = "#/$defs/" + prefix + obj["$ref"]!.ToString()[8..];
            foreach (var child in obj.Select(p => p.Value)) ScopeReferences(child, prefix);
        }
        else if (node is JsonArray array) foreach (var child in array) ScopeReferences(child, prefix);
    }

    public static JsonObject Values(PlanningWorkflow workflow, PlanningConstructionUnit unit, PlanningPreparation? preparation = null)
    {
        if (unit.Kind == "inputs") return new() { ["inputs"] = new JsonObject(workflow.Inputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name,
            new JsonObject { ["schema"] = Compact(p.Schema, PlanningJsonContext.Default.PlanningSchema), ["default"] = p.Default is null ? null : Compact(p.Default, PlanningJsonContext.Default.PlanningValue) }))) };
        if (unit.Kind == "outputs") return new() { ["outputs"] = new JsonObject(workflow.Outputs.Select(p => new KeyValuePair<string, JsonNode?>(p.Name,
            new JsonObject { ["reference"] = PlanningOutputBindings.Id(p.Value) }))) };
        var nodes = new JsonObject();
        foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => unit.NodeKeys.Contains(n.Key, StringComparer.Ordinal)))
        {
            var fields = new JsonObject();
            if (unit.Kind == "contracts")
            {
                if (node.Type == "set") fields["outputSchema"] = Compact(node.OutputSchema ?? new(), PlanningJsonContext.Default.PlanningSchema);
                if (node.Type is "mcp.call" or "llm.call") fields["structuredOutput"] = node.StructuredOutput is null ? null : new JsonObject { ["schema"] = Compact(node.StructuredOutput.Schema, PlanningJsonContext.Default.PlanningSchema) };
            }
            else
            {
                if (node.Type == "human.input") fields["context"] = node.Input.Members.FirstOrDefault(m => m.Name == "context") is { } context ? Compact(context.Value, PlanningJsonContext.Default.PlanningValue) : null;
                else if (preparation is not null && PlanningDecisionRouting.LocalContracts(node, preparation).Length > 0) fields["conditions"] = PlanningDecisionRouting.ConditionsValues(node, preparation);
                else if (node.Type is not ("sequence" or "parallel" or "switch")) fields["input"] = Compact(node.Input, PlanningJsonContext.Default.PlanningValue);
                if (node.Type == "switch" && node.Expr?.Kind is not ("confirmation" or "decision_binding")) fields["expr"] = Compact(node.Expr ?? new(), PlanningJsonContext.Default.PlanningValue);
                if (unit.ContractVersion < 11 && node.Type is "loop.sequential" or "loop.parallel") { fields["itemVar"] = node.ItemVar; fields["indexVar"] = node.IndexVar; }
                if (node.Type is not ("sequence" or "parallel" or "switch" or "human.input") && !fields.ContainsKey("conditions")) fields["onError"] = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(node, PlanningJsonContext.Default.PlanningNode)!["onError"]);
            }
            nodes[node.Key] = fields;
        }
        var result = new JsonObject { ["nodes"] = nodes };
        if (unit.Kind == "implementation") result["functions"] = unit.Functions;
        return result;
    }

    public static PlanningGraph Apply(PlanningGraph graph, PlanningConstructionUnit unit, JsonObject candidate, PlanningPreparation preparation)
    {
        var result = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        var workflow = result.Workflows.Single(w => w.Key == unit.WorkflowKey);
        var errors = ShapeFindings(candidate, Schema(workflow, unit, preparation, result), unit);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors.Select(d => d.Message)));
        if (unit.Kind == "inputs")
            foreach (var port in workflow.Inputs)
            {
                var value = candidate["inputs"]![port.Name]!;
                port.Schema = JsonSerializer.Deserialize(value["schema"]!, PlanningJsonContext.Default.PlanningSchema)!;
                port.Default = value["default"] is { } literal ? JsonSerializer.Deserialize(literal, PlanningJsonContext.Default.PlanningValue) : null;
            }
        else if (unit.Kind == "outputs")
        {
            var bindings = PlanningOutputBindings.Index(workflow, preparation, result);
            foreach (var port in workflow.Outputs)
            {
                var value = candidate["outputs"]![port.Name]!;
                var binding = bindings[value["reference"]!.GetValue<string>()];
                port.Value = binding.Value; port.Schema = binding.Schema;
            }
        }
        else foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => unit.NodeKeys.Contains(n.Key, StringComparer.Ordinal)))
        {
            var fields = candidate["nodes"]![node.Key]!.AsObject();
            if (unit.Kind == "implementation" && unit.ContractVersion >= PlanningDataflow.BindingVersion)
            {
                try { fields = PlanningDataflow.Expand(fields, PlanningDataflow.Index(workflow, preparation, result, node.Key), "/nodes/" + PlanningSchemaReferences.Escape(node.Key))!.AsObject(); }
                catch (PlanningDataflow.BindingException error)
                {
                    var original = graph.Workflows.Single(w => w.Key == unit.WorkflowKey);
                    var before = PlanningDataflow.Index(original, preparation, graph, node.Key).GetValueOrDefault(error.Reference);
                    var changedProducer = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == before?.Value.Source);
                    if (before?.Value.Kind == "output" && before.Value.ResultChannel is not "structured" && changedProducer?.OnError.Any(h => h.Action == "continue") == true && unit.NodeKeys.Contains(changedProducer.Key))
                        throw new PlanningDataflow.BindingException("/nodes/" + PlanningSchemaReferences.Escape(changedProducer.Key) + "/onError", error.Reference,
                            "This failure handler removes the original response contract required by a downstream binding. Fail closed at the producer while preserving cleanup, or establish an explicit guarded failure route. A fallback cannot manufacture a required original artifact.");
                    throw;
                }
            }
            if (unit.Kind == "contracts")
            {
                if (node.Type == "set") node.OutputSchema = JsonSerializer.Deserialize(fields["outputSchema"]!, PlanningJsonContext.Default.PlanningSchema);
                if (node.Type is "mcp.call" or "llm.call")
                    node.StructuredOutput = fields["structuredOutput"] is { } value ? new(NormalizeStructured(JsonSerializer.Deserialize(value["schema"]!, PlanningJsonContext.Default.PlanningSchema)!, preparation)) : null;
            }
            else
            {
                if (fields["arguments"] is JsonObject arguments)
                {
                    var request = new PlanningValue { Kind = "object" };
                    foreach (var (key, value) in arguments)
                        if (value?["kind"]?.ToString() != "omit") request.Members.Add(new(key, JsonSerializer.Deserialize(value!, PlanningJsonContext.Default.PlanningValue)!));
                    // A construction-contract upgrade changes argument addressing, not
                    // established transport/error behavior on the retained node.
                    node.Input = new() { Kind = "object", Members = node.Input.Members.Where(m => m.Name != "request").Append(new("request", request)).ToList() };
                    var capability = preparation.Capabilities.Single(c => c.Id == node.CapabilityId);
                    foreach (var binding in capability.RequestBindings.Where(b => b.Path.StartsWith('/') && !b.Path[1..].Contains('/')))
                        request.Members.Add(new(binding.Path[1..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal), Literal(binding.Value)));
                }
                if (fields["input"] is { } input) node.Input = JsonSerializer.Deserialize(input, PlanningJsonContext.Default.PlanningValue)!;
                if (fields["conditions"] is JsonObject conditions) PlanningDecisionRouting.ApplyConditions(workflow, node, conditions, preparation, result);
                if (node.Type == "human.input")
                {
                    node.Input = Literal(HumanInputContract.ConfirmationInput(node.Purpose));
                    if (fields["context"] is { } context) node.Input.Members.Add(new("context", JsonSerializer.Deserialize(context, PlanningJsonContext.Default.PlanningValue)!));
                }
                if (node.Type == "switch")
                {
                    node.Expr = PlanningDecisionRouting.Contract(node, preparation) is not null ? PlanningDecisionRouting.Resolve(workflow, node, preparation, result)
                        : JsonSerializer.Deserialize(fields["expr"]!, PlanningJsonContext.Default.PlanningValue);
                    // Accepted explicit outcomes are value matches. Default is never a numbered case.
                    node.Cases = node.Cases.Select(c => c with { When = null }).ToList();
                }
                if (node.Type is "loop.sequential" or "loop.parallel")
                {
                    node.ItemVar = unit.ContractVersion >= 11 ? PlanningGraphCompiler.LoopVariable(node.Key, false) : fields["itemVar"]?.GetValue<string>();
                    node.IndexVar = unit.ContractVersion >= 11 ? PlanningGraphCompiler.LoopVariable(node.Key, true) : fields["indexVar"]?.GetValue<string>();
                }
                if (fields["onError"] is JsonArray onError) node.OnError = onError.Select(e => JsonSerializer.Deserialize(e!, PlanningJsonContext.Default.PlanningErrorCase)!).ToList();
            }
        }
        return result;
    }

    internal static JsonObject UpgradeCandidate(PlanningGraph graph, PlanningConstructionUnit unit, JsonObject candidate, PlanningPreparation preparation)
    {
        var workflow = graph.Workflows.Single(w => w.Key == unit.WorkflowKey);
        var bindings = PlanningDataflow.Index(workflow, preparation, graph);
        foreach (var key in unit.NodeKeys)
            foreach (var binding in PlanningDataflow.Index(workflow, preparation, graph, key)) bindings.TryAdd(binding.Key, binding.Value);
        candidate = candidate.DeepClone().AsObject();
        if (unit.Kind == "implementation")
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => unit.NodeKeys.Contains(n.Key) && PlanningDecisionRouting.LocalContracts(n, preparation).Length > 0))
                if (candidate["nodes"]?[node.Key] is JsonObject fields && !fields.ContainsKey("conditions") && node.Input.Members.Any(m => m.Name == "decisions"))
                {
                    fields["conditions"] = PlanningDecisionRouting.ConditionsValues(node, preparation);
                    fields.Remove("input"); fields.Remove("onError");
                }
        var result = PlanningDataflow.Transport(candidate, bindings)!.AsObject();
        if (unit.Kind != "implementation") return result;
        if (unit.ContractVersion >= 11)
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => unit.NodeKeys.Contains(n.Key, StringComparer.Ordinal) && n.Type is "loop.sequential" or "loop.parallel"))
                if (result["nodes"]?[node.Key] is JsonObject loopFields) { loopFields.Remove("itemVar"); loopFields.Remove("indexVar"); }
        foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Where(n => unit.NodeKeys.Contains(n.Key, StringComparer.Ordinal) && n.Type == "mcp.call"))
        {
            var capability = preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId);
            if (capability?.InputSchema["properties"] is not JsonObject properties || result["nodes"]?[node.Key] is not JsonObject fields || fields["input"] is not JsonObject input) continue;
            var members = input["members"] as JsonArray;
            var request = members?.FirstOrDefault(m => m?["name"]?.ToString() == "request")?["value"]?["members"] as JsonArray;
            if (request is null) continue;
            var arguments = new JsonObject();
            foreach (var property in properties.Where(p => !capability.RequestBindings.Any(b => b.Path == "/" + PlanningSchemaReferences.Escape(p.Key))))
                arguments[property.Key] = request.FirstOrDefault(m => m?["name"]?.ToString() == property.Key)?["value"]?.DeepClone() ?? new JsonObject { ["kind"] = "omit" };
            // Keep undeclared arguments visible to schema validation and targeted repair.
            foreach (var member in request.Where(m => m?["name"] is not null && !properties.ContainsKey(m["name"]!.ToString())))
                arguments[member!["name"]!.ToString()] = member["value"]?.DeepClone();
            fields["arguments"] = arguments; fields.Remove("input");
        }
        return result;
    }

    internal static PlanningSchema NormalizeStructured(PlanningSchema schema, PlanningPreparation preparation)
    {
        if (schema.CapabilityId is not null)
        {
            // Authoritative references cannot be rewritten. They must already satisfy strictness.
            var errors = PlanningContractValidation.ValidateSchema(PlanningGraphCompiler.ToJsonSchema(schema, preparation), strict: true);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
            return schema;
        }
        foreach (var port in schema.Properties)
        {
            port.Schema = NormalizeStructured(port.Schema, preparation);
            if (!port.Required)
            {
                if (port.Schema.CapabilityId is not null) port.Schema = PlanningGraphImporter.Schema(PlanningGraphCompiler.ToJsonSchema(port.Schema, preparation));
                port.Required = true; port.Schema.Nullable = true;
            }
        }
        if (schema.Items is not null) schema.Items = NormalizeStructured(schema.Items, preparation);
        if (schema.AdditionalProperties is not null) schema.AdditionalProperties = NormalizeStructured(schema.AdditionalProperties, preparation);
        return schema;
    }

    internal static PlanningValue Literal(JsonNode? json) => json switch
    {
        null => new(), JsonObject obj => new() { Kind = "object", Members = obj.Select(p => new PlanningMember(p.Key, Literal(p.Value))).ToList() },
        JsonArray array => new() { Kind = "array", Items = array.Select(Literal).ToList() },
        JsonValue value when value.TryGetValue<string>(out var text) => new() { Kind = "string", Text = text },
        JsonValue value when value.TryGetValue<bool>(out var boolean) => new() { Kind = "boolean", Boolean = boolean },
        JsonValue value => new() { Kind = "number", Number = value.GetValue<decimal>() }, _ => throw new InvalidOperationException("Unsupported literal.")
    };

    public static List<PlanningDiagnostic> ShapeFindings(JsonObject? candidate, JsonObject schema, PlanningConstructionUnit unit) =>
        PlanningContractValidation.ValidateInstance(candidate, schema).Select(e => new PlanningDiagnostic("UNIT_RESPONSE_INVALID", "/units/" + PlanningSchemaReferences.Escape(unit.Key), e, ValidationStage: "conversion")).ToList();

    public static int EstimateInputTokens(string prompt, JsonObject schema) => checked((Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(schema.ToJsonString()) + 2) / 3 + 256);

    internal static void PruneDefinitions(JsonObject schema)
    {
        var definitions = schema["$defs"]!.AsObject(); var used = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? value)
        {
            if (value is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (value is not JsonObject obj) return;
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var name) && name.StartsWith("#/$defs/", StringComparison.Ordinal))
            { var key = name[8..]; if (used.Add(key)) Visit(definitions[key]); }
            foreach (var (key, child) in obj) if (key != "$defs") Visit(child);
        }
        Visit(schema);
        foreach (var key in definitions.Select(p => p.Key).Where(k => !used.Contains(k)).ToArray()) definitions.Remove(key);
    }
}
