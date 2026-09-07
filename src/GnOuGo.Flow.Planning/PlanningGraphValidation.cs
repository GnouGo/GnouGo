using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Checks schema provenance before review and again before deterministic export.</summary>
public static class PlanningGraphValidation
{
    public static IReadOnlyList<PlanningDiagnostic> Validate(PlanningGraph graph, PlanningPreparation preparation)
        => ValidateCore(graph, preparation, null);

    internal static Dictionary<string, JsonObject> DescribeResults(PlanningWorkflow workflow, PlanningPreparation preparation, PlanningGraph? graph = null)
    {
        var results = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        ValidateCore(graph ?? new PlanningGraph { Workflows = [workflow] }, preparation, results, workflow.Key);
        return results;
    }

    internal static JsonObject ResolveValueContract(PlanningGraph graph, PlanningWorkflow workflow, PlanningValue value, PlanningPreparation preparation)
        => ValueContractResolver(graph, workflow, preparation)(value);

    internal static Func<PlanningValue, JsonObject> ValueContractResolver(PlanningGraph graph, PlanningWorkflow workflow, PlanningPreparation preparation)
    {
        Func<PlanningValue, JsonObject>? resolver = null;
        ValidateCore(graph, preparation, null, workflow.Key, resolved: callback => resolver = callback);
        return resolver ?? throw new InvalidOperationException("The public output has no established workflow.");
    }

    private static IReadOnlyList<PlanningDiagnostic> ValidateCore(PlanningGraph graph, PlanningPreparation preparation, Dictionary<string, JsonObject>? results,
        string? targetWorkflow = null, Action<Func<PlanningValue, JsonObject>>? resolved = null)
    {
        var errors = new List<PlanningDiagnostic>();
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi];
            var path = "/workflows/" + wi;
            var nodes = Located(workflow.Steps, path + "/steps").Concat(Located(workflow.Finally, path + "/finally")).ToArray();
            var byKey = nodes.GroupBy(n => n.Node.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Node, StringComparer.Ordinal);
            var structured = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            for (var i = 0; i < workflow.Inputs.Count; i++) CheckSchema(workflow.Inputs[i].Schema, path + "/inputs/" + i + "/schema", true);
            for (var i = 0; i < workflow.Outputs.Count; i++) CheckSchema(workflow.Outputs[i].Schema, path + "/outputs/" + i + "/schema", true);
            foreach (var (node, location) in nodes)
            {
                if (node.CapabilityId is { } capabilityId)
                {
                    var binding = preparation.Capabilities.FirstOrDefault(c => c.Id == capabilityId);
                    if (binding is null || !PlanningCapabilityBindings.Supports(binding, node.Type))
                        errors.Add(new("CAPABILITY_BINDING_INVALID", location + "/capabilityId", "The node must implement its exact selected executor, or a permitted local-processing control-flow construct."));
                }
                if (node.OutputSchema is not null) CheckSchema(node.OutputSchema, location + "/outputSchema", false);
                var config = Member(node.Input, "structured_output");
                if (config is null && node.StructuredOutput is null) continue;
                try
                {
                    if (node.Type is not ("mcp.call" or "llm.call")) throw new InvalidOperationException("This step does not support structured_output.");
                    if (config is not null && node.StructuredOutput is not null) throw new InvalidOperationException("Declare structured output once, using the typed node declaration.");
                    var json = node.StructuredOutput is { } typed
                        ? new JsonObject { ["schema_inline"] = PlanningGraphCompiler.ToJsonSchema(typed.Schema, preparation), ["strict"] = typed.Strict }
                        : Literal(config!) as JsonObject ?? throw new InvalidOperationException("structured_output must be a literal configuration object.");
                    if (json.Any(p => p.Key is not ("schema_inline" or "schema_ref" or "strict")) ||
                        (json["schema_inline"] is not null) == (json["schema_ref"] is not null))
                        throw new InvalidOperationException("Declare exactly one structured-output schema and an optional strict flag.");
                    var schema = (json["schema_inline"] ?? json["schema_ref"]) as JsonObject
                        ?? throw new InvalidOperationException("The structured-output schema must resolve to a declared literal schema object.");
                    var strict = json["strict"]?.GetValue<bool>() ?? false;
                    var findings = PlanningContractValidation.ValidateSchema(schema, strict);
                    if (findings.Count != 0) throw new InvalidOperationException(string.Join("; ", findings));
                    RequireTyped(schema, 0);
                    structured[node.Key] = schema;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
                { errors.Add(new("STRUCTURED_OUTPUT_INVALID", location + (node.StructuredOutput is null ? "/input/structured_output" : "/structuredOutput"), ex.Message)); }
            }
            foreach (var (node, location) in nodes)
            {
                if (results is not null && workflow.Key == targetWorkflow)
                    try
                    {
                        if (ValueSchema(new() { Kind = "output", Source = node.Key }, new(StringComparer.Ordinal)) is { } contract)
                            results[node.Key] = contract;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { }
                try
                {
                    if (node.OutputSchema is not null)
                    {
                        var declared = PlanningGraphCompiler.ToJsonSchema(node.OutputSchema, preparation);
                        var actual = node.Type == "set" ? ValueSchema(node.Input, new(StringComparer.Ordinal)) : ValueSchema(new() { Kind = "output", Source = node.Key }, new(StringComparer.Ordinal));
                        if (actual is not null && !TypesFit(actual, declared, allowUnresolved: node.Type == "set")) errors.Add(node.Type == "set"
                            ? new("SET_OUTPUT_INVALID", location + "/input", "The set input is its result and does not satisfy the declared output contract. Compute the declared fields inside input; a context object cannot stand in for the calculation.")
                            : new("OUTPUT_TYPE_MISMATCH", location + "/outputSchema", "The declared output is not established by the actual computation or producer contract."));
                        if (node.Type == "set" && IsLiteral(node.Input))
                            errors.AddRange(PlanningContractValidation.ValidateInstance(Literal(node.Input), declared).Select(e => new PlanningDiagnostic("SET_OUTPUT_INVALID", location + "/input", e)));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { /* Independent fallback/reference diagnostics follow. */ }
                try
                {
                    if (structured.TryGetValue(node.Key, out var resultSchema))
                        for (var ei = 0; ei < node.OnError.Count; ei++)
                            if (node.OnError[ei].SetOutput is { } fallback)
                            {
                                var fallbackSchema = ValueSchema(fallback, new(StringComparer.Ordinal));
                                if (fallbackSchema?["properties"]?["json"] is not JsonObject jsonSchema || !TypesFit(jsonSchema, resultSchema))
                                {
                                    var memberIndex = fallback.Members.FindIndex(m => m.Name == "json");
                                    errors.Add(new("STRUCTURED_FALLBACK_INVALID", location + "/onError/" + ei + "/setOutput" + (memberIndex < 0 ? "" : "/members/" + memberIndex + "/value"),
                                        "The fallback json member must satisfy the structured result contract (type " + resultSchema["type"]?.ToJsonString() + "). Structured objects remain objects, not serialized JSON strings; raw tool results belong in response."));
                                }
                            }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { /* Dedicated reference diagnostics follow. */ }
                CheckValue(node.Input, location + "/input");
                if (node.Type == "mcp.call" && preparation.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId) is { } capability &&
                    capability.InputSchema["properties"] is JsonObject argumentSchemas && Member(node.Input, "request") is { Kind: "object" } arguments)
                {
                    var requestIndex = node.Input.Members.FindIndex(m => m.Name == "request");
                    var requestLocation = location + "/input/members/" + requestIndex + "/value";
                    foreach (var name in (capability.InputSchema["required"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
                        if (!arguments.Members.Any(m => m.Name == name) && !capability.RequestBindings.Any(b => b.Path == "/" + PlanningSchemaReferences.Escape(name)))
                            errors.Add(new("CAPABILITY_ARGUMENT_MISSING", requestLocation, "The selected capability requires argument '" + name + "'."));
                    for (var ai = 0; ai < arguments.Members.Count; ai++)
                    {
                        var member = arguments.Members[ai]; var field = requestLocation + "/members/" + ai + "/value";
                        if (argumentSchemas[member.Name] is not JsonObject expected)
                        {
                            if (capability.InputSchema["additionalProperties"]?.ToString() == "false") errors.Add(new("CAPABILITY_ARGUMENT_UNKNOWN", field, "This capability does not declare argument '" + member.Name + "'."));
                            continue;
                        }
                        try
                        {
                            if (IsLiteral(member.Value))
                                errors.AddRange(PlanningContractValidation.ValidateInstance(Literal(member.Value), expected).Select(e => new PlanningDiagnostic("CAPABILITY_ARGUMENT_INVALID", field, e)));
                            else if (ValueSchema(member.Value, new(StringComparer.Ordinal)) is { } actual && !TypesFit(actual, expected))
                                errors.Add(new("CAPABILITY_ARGUMENT_TYPE", field, "The binding's producer type does not satisfy argument '" + member.Name + "'. Use an explicit validated transformation."));
                        }
                        catch (InvalidOperationException) { /* The reference diagnostic identifies unresolved producers. */ }
                    }
                }
                if (node.If is not null) CheckValue(node.If, location + "/if");
                if (node.Expr is not null) CheckValue(node.Expr, location + "/expr");
                for (var i = 0; i < node.Cases.Count; i++)
                    if (node.Cases[i].When is { } when) CheckValue(when, location + "/cases/" + i + "/when");
                for (var i = 0; i < node.OnError.Count; i++)
                {
                    if (node.OnError[i].If is { } condition) CheckValue(condition, location + "/onError/" + i + "/if");
                    if (node.OnError[i].SetOutput is { } value) CheckValue(value, location + "/onError/" + i + "/setOutput");
                }
            }
            for (var i = 0; i < workflow.Inputs.Count; i++)
                if (workflow.Inputs[i].Default is { } value) CheckValue(value, path + "/inputs/" + i + "/default");
            for (var i = 0; i < workflow.Outputs.Count; i++)
            {
                var output = workflow.Outputs[i];
                var location = path + "/outputs/" + i;
                CheckValue(output.Value, location + "/value");
                if (output.Value.Kind is not ("input" or "output")) continue;
                try
                {
                    var actual = ValueSchema(output.Value, new(StringComparer.Ordinal));
                    var expected = PlanningGraphCompiler.ToJsonSchema(output.Schema, preparation);
                    if (actual is not null && !TypesFit(actual, expected))
                        errors.Add(new("OUTPUT_TYPE_MISMATCH", location + "/schema", "The boundary type is not established by its producer contract."));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { /* The reference/schema diagnostic is reported at its own location. */ }
            }

            if (workflow.Key == targetWorkflow) resolved?.Invoke(value => ValueSchema(value, new(StringComparer.Ordinal)) ?? throw new InvalidOperationException("The public output has no established producer contract."));

            void CheckValue(PlanningValue value, string location)
            {
                if (value.Kind == "workflow" && !graph.Workflows.Any(w => w.Key == value.Source))
                    errors.Add(new("WORKFLOW_REFERENCE_INVALID", location + "/source", "The workflow reference has no declared workflow. Input ports require input references; step results require output references."));
                if (value.ResultChannel is not (null or "default" or "structured") || (value.ResultChannel is not null && value.Kind != "output"))
                    errors.Add(new("RESULT_CHANNEL_INVALID", location + "/resultChannel", "Only output references select default or structured results."));
                if (value.Kind is "output" or "input")
                {
                    try { _ = ValueSchema(value, new(StringComparer.Ordinal)); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException) { errors.Add(new("OUTPUT_REFERENCE_INVALID", location, ex.Message)); }
                }
                for (var i = 0; i < value.Members.Count; i++) CheckValue(value.Members[i].Value, location + "/members/" + i + "/value");
                for (var i = 0; i < value.Items.Count; i++) CheckValue(value.Items[i], location + "/items/" + i);
            }

            JsonObject? ValueSchema(PlanningValue value, HashSet<string> visiting)
            {
                if (value.Kind == "input")
                {
                    var input = workflow.Inputs.FirstOrDefault(p => p.Name == value.Source)
                        ?? throw new InvalidOperationException("The input reference has no declared input producer.");
                    return AtPath(PlanningGraphCompiler.ToJsonSchema(input.Schema, preparation), value.Path);
                }
                if (value.Kind == "output")
                {
                    if (value.Source is null || !byKey.TryGetValue(value.Source, out var producer))
                        throw new InvalidOperationException("The output reference has no declared node producer. Use the node key, not an output alias.");
                    if (!visiting.Add(producer.Key)) throw new InvalidOperationException("The output contract contains a cyclic producer reference.");
                    try
                    {
                        JsonObject? schema;
                        if (value.ResultChannel == "structured")
                        {
                            if (!structured.TryGetValue(producer.Key, out schema))
                                throw new InvalidOperationException("The producer has no valid structured-output contract. Declare it before selecting the structured channel.");
                        }
                        else if (producer.Type == "mcp.call")
                        {
                            schema = preparation.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId)?.OutputSchema;
                            foreach (var handler in producer.OnError.Where(h => h.Action == "continue"))
                            {
                                var response = handler.SetOutput is { } fallback ? Member(fallback, "response") : null;
                                if (response is null || schema is { Count: > 0 } && (ValueSchema(response, visiting) is not { } fallbackContract || !TypesFit(fallbackContract, schema)))
                                    throw new InvalidOperationException("The producer can continue without its declared raw response contract. Preserve response in the fallback or select a validated structured channel.");
                            }
                        }
                        else if (producer.Type == "workflow.call")
                        {
                            var target = graph.Workflows.FirstOrDefault(w => w.Key == Member(producer.Input, "ref")?.Source);
                            schema = target is null ? null : ObjectSchema(target.Outputs.Select(o => (o.Name, PlanningGraphCompiler.ToJsonSchema(o.Schema, preparation))));
                        }
                        else if (producer.Type == "set") schema = producer.OutputSchema is not null ? PlanningGraphCompiler.ToJsonSchema(producer.OutputSchema, preparation) : ValueSchema(producer.Input, visiting);
                        else if (producer.Type == "sequence") schema = ChildSchema(producer.Steps, visiting);
                        else if (producer.Type == "switch")
                        {
                            var selected = producer.Expr is { Kind: "string" } literal ? producer.Cases.FirstOrDefault(c => c.Value == literal.Text) : null;
                            if (selected is not null) schema = ChildSchema(selected.Steps, visiting);
                            else
                            {
                                var alternatives = producer.Cases.Select(c => (JsonNode?)ChildSchema(c.Steps, visiting)).ToList();
                                alternatives.Add(producer.Default.Count == 0 ? new JsonObject { ["type"] = "null" } : ChildSchema(producer.Default, visiting));
                                schema = new() { ["anyOf"] = new JsonArray(alternatives.ToArray()) };
                            }
                        }
                        else if (producer.Type is "loop.sequential" or "loop.parallel")
                        {
                            var children = PlanningGraphCompiler.Enumerate(producer.Steps).ToArray();
                            schema = ObjectSchema([("count", new JsonObject { ["type"] = "integer" }), ("results", new JsonObject { ["type"] = "array", ["items"] = ObjectSchema(children.Select(n => (n.Key, Envelope(n, visiting)))) })]);
                        }
                        else if (producer.Type == "human.input")
                            schema = HumanSchema(producer.Input);
                        else if (producer.Type == "decision.evaluate")
                            schema = PlanningDecisionRouting.OutputSchema(producer, preparation) ?? preparation.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId)?.OutputSchema;
                        else schema = preparation.Capabilities.FirstOrDefault(c => c.Id == producer.CapabilityId)?.OutputSchema ?? BuiltInStepContracts.Get(producer.Type)?.OutputSchema;
                        if (schema is null) throw new InvalidOperationException("The producer needs an explicit typed output contract.");
                        return AtPath(schema, value.Path);
                    }
                    finally { visiting.Remove(producer.Key); }
                }
                return value.Kind switch
                {
                    "string" => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(value.Text ?? "") },
                    "number" => new JsonObject { ["type"] = value.Number is { } n && n == decimal.Truncate(n) ? "integer" : "number" },
                    "boolean" => new JsonObject { ["type"] = "boolean" },
                    "template" => new JsonObject { ["type"] = "string" },
                    "null" => new JsonObject { ["type"] = "null" },
                    "object" => ObjectSchema(value.Members.Select(m => (m.Name, ValueSchema(m.Value, visiting) ?? new JsonObject()))),
                    "array" when value.Items.Count == 0 => new JsonObject { ["type"] = "array", ["maxItems"] = 0 },
                    "array" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["anyOf"] = new JsonArray(value.Items.Select(v => (JsonNode?)(ValueSchema(v, visiting) ?? new JsonObject())).ToArray()) } },
                    _ => null
                };
            }

            JsonObject ChildSchema(IEnumerable<PlanningNode> children, HashSet<string> visiting)
            {
                var nodes = children.ToArray();
                var result = ObjectSchema(nodes.Select(n => (n.Key, Envelope(n, visiting))));
                result["required"] = new JsonArray(nodes.Where(n => n.If is null).Select(n => (JsonNode?)JsonValue.Create(n.Key)).ToArray());
                return result;
            }

            JsonObject Envelope(PlanningNode node, HashSet<string> visiting)
            {
                var schema = ValueSchema(new() { Kind = "output", Source = node.Key }, visiting) ?? new JsonObject();
                if (node.Type == "mcp.call") schema = ObjectSchema([("response", schema)]);
                else if (node.Type == "workflow.call") schema = ObjectSchema([("outputs", schema)]);
                if (structured.TryGetValue(node.Key, out var json))
                {
                    schema = (JsonObject)schema.DeepClone();
                    schema["properties"] ??= new JsonObject(); schema["properties"]!["json"] = json.DeepClone();
                }
                return schema;
            }
        }
        return errors.DistinctBy(d => (d.Code, d.Location, d.Message)).ToArray();

        void CheckSchema(PlanningSchema schema, string location, bool boundary)
        {
            try
            {
                var json = PlanningGraphCompiler.ToJsonSchema(schema, preparation);
                var findings = PlanningContractValidation.ValidateSchema(json);
                if (findings.Count > 0) throw new InvalidOperationException(string.Join("; ", findings));
                if (boundary) { RequireTyped(json, 0); _ = PlanningGraphCompiler.ToFlowSchema(json); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            { errors.Add(new(schema.CapabilityId is not null || schema.SchemaPointer is not null ? "SCHEMA_REFERENCE_INVALID" : "SCHEMA_INVALID", location, ex.Message)); }
            // Collect nested failures even when an enclosing schema failed first.
            for (var i = 0; i < schema.Properties.Count; i++) CheckSchema(schema.Properties[i].Schema, location + "/properties/" + i + "/schema", false);
            if (schema.Items is not null) CheckSchema(schema.Items, location + "/items", false);
            if (schema.AdditionalProperties is not null) CheckSchema(schema.AdditionalProperties, location + "/additionalProperties", false);
        }
    }

    private static bool TypesFit(JsonObject actual, JsonObject expected, int depth = 0, bool allowUnresolved = false)
    {
        if (depth > 32) return false;
        if (allowUnresolved && actual.Count == 0) return true; // set enforces the asserted schema at runtime
        if ((actual["anyOf"] ?? actual["oneOf"]) is JsonArray variants)
            return variants.Count != 0 && variants.All(v => v is JsonObject variant && TypesFit(variant, expected, depth + 1, allowUnresolved));
        static string[] Types(JsonNode? node) => node is JsonArray a ? a.Select(n => n!.GetValue<string>()).ToArray() : node is JsonValue v ? [v.GetValue<string>()] : [];
        var source = Types(actual["type"]); var target = Types(expected["type"]);
        if (source.Length == 0 || !source.All(t => target.Contains(t, StringComparer.Ordinal) || t == "integer" && target.Contains("number", StringComparer.Ordinal))) return false;
        if (expected["enum"] is JsonArray allowed && (actual["enum"] is not JsonArray declared || declared.Any(value => !allowed.Any(option => JsonNode.DeepEquals(option, value))))) return false;
        if (expected["properties"] is JsonObject properties)
        {
            if (expected["additionalProperties"] is JsonValue extra && extra.TryGetValue<bool>(out var allowedExtra) && !allowedExtra &&
                actual["properties"] is JsonObject actualProperties && actualProperties.Any(p => !properties.ContainsKey(p.Key))) return false;
            var required = (expected["required"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, property) in properties)
            {
                var produced = actual["properties"]?[name] as JsonObject ?? actual["additionalProperties"] as JsonObject;
                if (produced is null) { if (required.Contains(name)) return false; }
                else if (property is not JsonObject contract || !TypesFit(produced, contract, depth + 1, allowUnresolved)) return false;
            }
        }
        var emptyArray = actual["maxItems"] is JsonValue maximum && maximum.TryGetValue<int>(out var count) && count == 0;
        if (expected["items"] is JsonObject items && !emptyArray &&
            (actual["items"] is not JsonObject producedItems || !TypesFit(producedItems, items, depth + 1, allowUnresolved))) return false;
        return true;
    }

    private static JsonObject AtPath(JsonObject root, List<string> path)
    {
        if ((root["anyOf"] ?? root["oneOf"]) is JsonArray alternatives && path.Count > 0)
            return new() { ["anyOf"] = new JsonArray(alternatives.Select(a => (JsonNode?)AtPath(a!.AsObject(), path)).ToArray()) };
        var current = root;
        foreach (var segment in path)
        {
            var count = 0;
            while (current["$ref"] is JsonValue reference)
            {
                if (++count > 32 || !reference.TryGetValue<string>(out var text) || !text.StartsWith("#/", StringComparison.Ordinal) || PlanningSchemaReferences.Read(root, text[1..]) is not JsonObject resolved)
                    throw new InvalidOperationException("The producer schema reference cannot be resolved without losing constraints.");
                current = resolved;
            }
            if (current["properties"]?[segment] is not null && !(current["required"] as JsonArray ?? []).Any(n => n?.GetValue<string>() == segment))
                throw new InvalidOperationException("The selected field is optional in its producer contract. Guard its presence or use a validated transformation before requiring it.");
            current = current["properties"]?[segment] as JsonObject ??
                (int.TryParse(segment, out var index) && index >= 0 ? current["items"] as JsonObject : null) ??
                current["additionalProperties"] as JsonObject ??
                throw new InvalidOperationException("The selected result field is not declared by the producer. Use a declared field or a validated structured/local transformation.");
        }
        return current;
    }

    internal static PlanningValue? Member(PlanningValue value, string name) => value.Members.FirstOrDefault(m => m.Name == name)?.Value;

    internal static bool IsLiteral(PlanningValue value) => value.Kind is "null" or "string" or "number" or "boolean" || value.Kind == "object" && value.Members.All(m => IsLiteral(m.Value)) || value.Kind == "array" && value.Items.All(IsLiteral);

    private static JsonObject HumanSchema(PlanningValue input)
    {
        return HumanInputContract.ResolveOutputSchema(new JsonObject(input.Members.Where(m => m.Name is "mode" or "choices" or "fields")
            .Select(m => new KeyValuePair<string, JsonNode?>(m.Name, Literal(m.Value)))));
    }

    internal static JsonNode? Literal(PlanningValue value) => value.Kind switch
    {
        "null" => null, "string" when value.Text?.Contains("${", StringComparison.Ordinal) != true => JsonValue.Create(value.Text ?? ""),
        "number" => JsonValue.Create(value.Number), "boolean" => JsonValue.Create(value.Boolean),
        "object" => new JsonObject(value.Members.Select(m => new KeyValuePair<string, JsonNode?>(m.Name, Literal(m.Value)))),
        "array" => new JsonArray(value.Items.Select(Literal).ToArray()),
        _ => throw new InvalidOperationException("A schema configuration requires literals, not data references or expressions.")
    };

    private static JsonObject ObjectSchema(IEnumerable<(string Name, JsonObject Schema)> properties)
    {
        var members = properties.ToArray();
        return new() { ["type"] = "object", ["properties"] = new JsonObject(members.Select(p => new KeyValuePair<string, JsonNode?>(p.Name, p.Schema.DeepClone()))),
            ["required"] = new JsonArray(members.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()) };
    }

    private static bool HasType(JsonNode? type, string name) => type is JsonValue scalar && scalar.TryGetValue<string>(out var value) && value == name ||
        type is JsonArray union && union.Any(t => t is JsonValue item && item.TryGetValue<string>(out var candidate) && candidate == name);

    internal static void RequireTyped(JsonObject schema, int depth)
    {
        if (depth > 32) throw new InvalidOperationException("Schema nesting exceeds 32 levels.");
        var type = schema["type"];
        if (type is null && schema["$ref"] is null && schema["anyOf"] is null && schema["oneOf"] is null)
            throw new InvalidOperationException("An output schema requires a concrete type.");
        if (HasType(type, "object"))
        {
            var properties = schema["properties"] as JsonObject;
            if ((properties is null || properties.Count == 0) && schema["additionalProperties"] is not JsonObject)
                throw new InvalidOperationException("An object output requires declared properties or typed additional properties; required names alone are insufficient.");
        }
        if (HasType(type, "array") && schema["items"] is not JsonObject)
            throw new InvalidOperationException("An array output requires typed items.");
        foreach (var child in (schema["properties"] as JsonObject ?? []).Select(p => p.Value).OfType<JsonObject>()) RequireTyped(child, depth + 1);
        if (schema["items"] is JsonObject items) RequireTyped(items, depth + 1);
        if (schema["additionalProperties"] is JsonObject additional) RequireTyped(additional, depth + 1);
    }

    internal static IEnumerable<(PlanningNode Node, string Path)> Located(List<PlanningNode> nodes, string path)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i]; var location = path + "/" + i;
            yield return (node, location);
            foreach (var child in Located(node.Steps, location + "/steps").Concat(Located(node.Default, location + "/default"))) yield return child;
            for (var b = 0; b < node.Branches.Count; b++) foreach (var child in Located(node.Branches[b].Steps, location + "/branches/" + b + "/steps")) yield return child;
            for (var c = 0; c < node.Cases.Count; c++) foreach (var child in Located(node.Cases[c].Steps, location + "/cases/" + c + "/steps")) yield return child;
        }
    }
}
