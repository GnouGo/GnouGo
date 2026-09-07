using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Parsing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace GnOuGo.Flow.Planning;

/// <summary>Deterministic lowering. Planning metadata has no route into executable YAML.</summary>
public sealed partial class PlanningGraphCompiler
{
    public static string Fingerprint(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Fingerprint(PlanningGraph graph) => Fingerprint(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph));

    public string Compile(PlanningGraph graph, PlanningPreparation preparation, string name = "generated")
    {
        ArgumentNullException.ThrowIfNull(graph);
        var diagnostics = PlanningExecutableValidation.Validate(graph, preparation);
        if (diagnostics.Count != 0) throw new InvalidOperationException(string.Join("; ", diagnostics.Select(d => d.Code + " at " + d.Location + ": " + d.Message)));
        if (graph.Workflows.Count == 0 || graph.Workflows.Count > 100)
            throw new InvalidOperationException("A planning graph must contain between 1 and 100 workflows.");
        EnsureUnique(graph.Workflows.Select(w => w.Key), "workflow");
        if (!graph.Workflows.Any(w => w.Key == graph.Entrypoint))
            throw new InvalidOperationException("The graph entrypoint is missing.");
        var workflowIds = graph.Workflows.ToDictionary(w => w.Key, w => w.Key == graph.Entrypoint ? "main" : "w_" + Fingerprint(w.Key)[..16], StringComparer.Ordinal);
        var root = new JsonObject { ["version"] = 1, ["name"] = name, ["entrypoint"] = "main" };
        if (!string.IsNullOrWhiteSpace(graph.Functions)) root["functions"] = graph.Functions;
        var workflows = new JsonObject();
        foreach (var workflow in graph.Workflows)
        {
            var allNodes = Enumerate(workflow.Steps).Concat(Enumerate(workflow.Finally)).ToArray();
            EnsureUnique(allNodes.Select(n => n.Key), "node");
            if (allNodes.Length > 300) throw new InvalidOperationException("A workflow exceeds the 300-node planning limit.");
            var nodeIds = allNodes.ToDictionary(n => n.Key, n => "n_" + Fingerprint(n.Key)[..16], StringComparer.Ordinal);
            var scope = new LoweringScope(preparation, nodeIds, workflowIds, workflow.Inputs.Select(p => p.Name).ToHashSet(StringComparer.Ordinal), allNodes.ToDictionary(n => n.Key, n => n.Type, StringComparer.Ordinal));
            var lowered = new JsonObject();
            if (workflow.Inputs.Count > 0)
            {
                EnsureUnique(workflow.Inputs.Select(p => p.Name), "input");
                var inputs = new JsonObject();
                foreach (var port in workflow.Inputs)
                {
                    var schema = LowerSchema(port.Schema, preparation);
                    schema["required"] = port.Required;
                    if (port.Default is not null) schema["default"] = LowerValue(port.Default, scope, allowReferences: false);
                    inputs[port.Name] = schema;
                }
                lowered["inputs"] = inputs;
            }
            if (!string.IsNullOrWhiteSpace(workflow.Functions)) lowered["functions"] = workflow.Functions;
            lowered["steps"] = LowerSteps(workflow.Steps, scope);
            if (workflow.Finally.Count > 0) lowered["finally"] = LowerSteps(workflow.Finally, scope);
            if (workflow.Outputs.Count > 0)
            {
                EnsureUnique(workflow.Outputs.Select(p => p.Name), "output");
                var outputs = new JsonObject();
                foreach (var output in workflow.Outputs)
                {
                    var schema = LowerSchema(output.Schema, preparation);
                    schema["expr"] = ToExpression(output.Value, scope);
                    outputs[output.Name] = schema;
                }
                lowered["outputs"] = outputs;
            }
            workflows[workflowIds[workflow.Key]] = lowered;
        }
        root["workflows"] = workflows;
        var main = (JsonObject)workflows["main"]!;
        root["skill"] = new JsonObject
        {
            ["description"] = graph.Summary,
            ["inputs"] = main["inputs"]?.DeepClone() ?? new JsonObject(),
            ["outputs"] = main["outputs"]?.DeepClone() ?? new JsonObject()
        };
        var stream = new YamlStream(new YamlDocument(ToYaml(root)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        stream.Save(writer, assignAnchors: false);
        var yaml = writer.ToString();
        new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return yaml;
    }

    public static IEnumerable<PlanningNode> Enumerate(IEnumerable<PlanningNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Enumerate(node.Steps.Concat(node.Default).Concat(node.Branches.SelectMany(b => b.Steps)).Concat(node.Cases.SelectMany(c => c.Steps))))
                yield return child;
        }
    }

    internal static IReadOnlyList<PlanningDiagnostic> ValidateValues(PlanningGraph graph, PlanningPreparation preparation)
    {
        var errors = new List<PlanningDiagnostic>();
        if (graph.Workflows.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != graph.Workflows.Count) return errors;
        var workflowIds = graph.Workflows.ToDictionary(w => w.Key, w => w.Key == graph.Entrypoint ? "main" : "w_" + Fingerprint(w.Key)[..16], StringComparer.Ordinal);
        for (var wi = 0; wi < graph.Workflows.Count; wi++)
        {
            var workflow = graph.Workflows[wi]; var path = "/workflows/" + wi;
            var nodes = Enumerate(workflow.Steps.Concat(workflow.Finally)).ToArray();
            if (nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != nodes.Length) continue;
            var scope = new LoweringScope(preparation, nodes.ToDictionary(n => n.Key, n => "n_" + Fingerprint(n.Key)[..16], StringComparer.Ordinal), workflowIds,
                workflow.Inputs.Select(p => p.Name).ToHashSet(StringComparer.Ordinal), nodes.ToDictionary(n => n.Key, n => n.Type, StringComparer.Ordinal));
            void Check(PlanningValue? value, string location, bool expression = false, bool literal = false)
            {
                if (value is null) return;
                try { if (expression) ToExpression(value, scope); else LowerValue(value, scope, !literal); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or GnOuGo.Flow.Core.Expressions.ExpressionParseException or Acornima.ParseErrorException)
                { errors.Add(new("VALUE_LOWERING_INVALID", location, ex.Message, ValidationStage: "conversion")); }
            }
            foreach (var (node, location) in PlanningGraphValidation.Located(workflow.Steps, path + "/steps").Concat(PlanningGraphValidation.Located(workflow.Finally, path + "/finally")))
            {
                Check(node.Input, location + "/input"); Check(node.Expr, location + "/expr", true); Check(node.If, location + "/if", true);
                for (var i = 0; i < node.Cases.Count; i++) Check(node.Cases[i].When, location + "/cases/" + i + "/when", true);
                for (var i = 0; i < node.OnError.Count; i++) { Check(node.OnError[i].If, location + "/onError/" + i + "/if", true); Check(node.OnError[i].SetOutput, location + "/onError/" + i + "/setOutput"); }
            }
            for (var i = 0; i < workflow.Inputs.Count; i++) Check(workflow.Inputs[i].Default, path + "/inputs/" + i + "/default", literal: true);
            for (var i = 0; i < workflow.Outputs.Count; i++) Check(workflow.Outputs[i].Value, path + "/outputs/" + i + "/value", true);
        }
        return errors;
    }

    private static JsonArray LowerSteps(List<PlanningNode> nodes, LoweringScope scope)
        => new(nodes.Select(n => (JsonNode)LowerNode(n, scope)).ToArray());

    private static JsonObject LowerNode(PlanningNode node, LoweringScope scope)
    {
        if (!scope.Preparation.AllowedStepTypes.Contains(node.Type, StringComparer.Ordinal))
            throw new InvalidOperationException("A node uses a step type outside the locked policy.");
        var result = new JsonObject { ["id"] = scope.NodeIds[node.Key], ["type"] = node.Type };
        var loweredInput = LowerValue(node.Input, scope);
        var computedSetInput = node.Type == "set" && node.Input.Kind is "expression" or "compute" or "input" or "output";
        var input = loweredInput as JsonObject ?? (computedSetInput ? new JsonObject() : throw new InvalidOperationException("A step input must be a typed object."));
        if (node.CapabilityId is { Length: > 0 })
        {
            var capability = scope.Preparation.Capabilities.SingleOrDefault(c => c.Id == node.CapabilityId)
                ?? throw new InvalidOperationException("Unknown capability reference.");
            if (!PlanningCapabilityBindings.Supports(capability, node.Type)) throw new InvalidOperationException("The node does not implement its selected capability type.");
            if (computedSetInput && capability.FixedInput.Count != 0) throw new InvalidOperationException("Locked input fields require an explicit object input.");
            foreach (var (key, value) in capability.FixedInput)
            {
                if (input[key] is not null && !JsonNode.DeepEquals(input[key], value))
                    throw new InvalidOperationException("A node changed a locked capability binding.");
                input[key] = value?.DeepClone();
            }
            if (node.Type == "mcp.call")
            {
                input["preserve_optional_nulls"] ??= true;
                Lock(input, "server", capability.Server);
                Lock(input, "method", capability.Method);
                if (capability.Kind is { Length: > 0 }) Lock(input, "kind", capability.Kind);
                foreach (var binding in capability.RequestBindings)
                {
                    var request = input["request"] as JsonObject;
                    if (request is null) input["request"] = request = new JsonObject();
                    ApplyBinding(request, binding);
                }
            }
        }
        else if (node.Type == "mcp.call") throw new InvalidOperationException("An external call must reference a locked capability.");
        if (computedSetInput) result["input"] = loweredInput;
        else if (input.Count > 0) result["input"] = input;
        if (node.If is not null) result["if"] = ToExpression(node.If, scope);
        if (node.Expr is not null) result["expr"] = ToExpression(node.Expr, scope);
        if (node.OutputSchema is not null && node.Type == "set") result["output_schema"] = ToJsonSchema(node.OutputSchema, scope.Preparation);
        if (node.StructuredOutput is { } structured)
        {
            if (input.ContainsKey("structured_output")) throw new InvalidOperationException("Use one typed structured-output declaration, not a second input schema.");
            input["structured_output"] = new JsonObject { ["schema_inline"] = ToJsonSchema(structured.Schema, scope.Preparation), ["strict"] = structured.Strict };
            result["input"] = input;
        }
        if (node.Output is not null) result["output"] = node.Output;
        if (node.ItemVar is not null) result["item_var"] = node.ItemVar;
        if (node.IndexVar is not null) result["index_var"] = node.IndexVar;
        if (node.Retry is not null) result["retry"] = Retry(node.Retry);
        if (node.OnError.Count > 0)
            result["on_error"] = new JsonObject { ["cases"] = new JsonArray(node.OnError.Select(c =>
            {
                if (c.Action is not ("stop" or "continue")) throw new InvalidOperationException("Unknown error action.");
                var errorCase = new JsonObject { ["action"] = c.Action };
                if (c.If is not null) errorCase["if"] = ToExpression(c.If, scope);
                if (c.SetOutput is not null) errorCase["set_output"] = LowerValue(c.SetOutput, scope);
                if (c.Retry is not null) errorCase["retry"] = Retry(c.Retry);
                return (JsonNode)errorCase;
            }).ToArray()) };
        if (node.Steps.Count > 0) result["steps"] = LowerSteps(node.Steps, scope);
        if (node.Branches.Count > 0) result["branches"] = new JsonArray(node.Branches.Select(b => (JsonNode)new JsonObject { ["steps"] = LowerSteps(b.Steps, scope) }).ToArray());
        if (node.Cases.Count > 0) result["cases"] = new JsonArray(node.Cases.Select(c =>
        {
            var branch = new JsonObject { ["steps"] = LowerSteps(c.Steps, scope) };
            if (c.Value is not null) branch["value"] = c.Value;
            if (c.When is not null) branch["when"] = ToExpression(c.When, scope);
            return (JsonNode)branch;
        }).ToArray());
        if (node.Default.Count > 0 || node.Type == "switch") result["default"] = LowerSteps(node.Default, scope);
        return result;
    }

    private static void Lock(JsonObject input, string key, string? value)
    {
        if (value is null) throw new InvalidOperationException("An external capability is incomplete.");
        if (input[key] is not null && input[key]!.GetValue<string>() != value)
            throw new InvalidOperationException("A node changed a locked external binding.");
        input[key] = value;
    }

    private static void ApplyBinding(JsonObject request, PlanningLiteralBinding binding)
    {
        if (!binding.Path.StartsWith("/", StringComparison.Ordinal)) throw new InvalidOperationException("A locked request binding must be a JSON pointer.");
        var parts = binding.Path[1..].Split('/').Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        if (parts.Length == 0) throw new InvalidOperationException("A locked request binding has an invalid path.");
        var current = request;
        foreach (var part in parts[..^1])
        {
            if (current[part] is null) current[part] = new JsonObject();
            current = current[part] as JsonObject ?? throw new InvalidOperationException("A locked request binding requires an object.");
        }
        var property = parts[^1];
        if (current[property] is not null && !JsonNode.DeepEquals(current[property], binding.Value)) throw new InvalidOperationException("A node changed a locked request selector.");
        current[property] = binding.Value?.DeepClone();
    }

    internal static JsonNode? ReadPointer(JsonNode? node, string pointer) => PlanningSchemaReferences.Read(node, pointer);

    private static JsonObject Retry(Core.Models.RetryPolicy retry) => new()
    {
        ["max"] = retry.Max, ["backoff_ms"] = retry.BackoffMs, ["backoff_mult"] = retry.BackoffMult, ["jitter_ms"] = retry.JitterMs
    };

    private static JsonNode? LowerValue(PlanningValue value, LoweringScope scope, bool allowReferences = true, int depth = 0)
    {
        if (depth > 64) throw new InvalidOperationException("Planning value nesting exceeds 64 levels.");
        switch (value.Kind)
        {
            case "null": return null;
            case "string":
                if (value.Text?.Contains("${", StringComparison.Ordinal) == true)
                    throw new InvalidOperationException("Interpolation requires an explicit expression value.");
                return JsonValue.Create(value.Text ?? "");
            case "number":
                var number = value.Number ?? throw new InvalidOperationException("Missing number.");
                if (number is > 9007199254740991m or < -9007199254740991m)
                    throw new InvalidOperationException("A numeric literal exceeds Flow's exact numeric range. Represent identifiers requiring larger exact integers as strings.");
                return JsonValue.Create(number);
            case "boolean": return JsonValue.Create(value.Boolean ?? throw new InvalidOperationException("Missing boolean."));
            case "object":
                EnsureUnique(value.Members.Select(m => m.Name), "member");
                var obj = new JsonObject();
                foreach (var member in value.Members) obj[member.Name] = LowerValue(member.Value, scope, allowReferences, depth + 1);
                return obj;
            case "array": return new JsonArray(value.Items.Select(v => LowerValue(v, scope, allowReferences, depth + 1)).ToArray());
            case "workflow" when allowReferences:
                if (value.Source is null || !scope.WorkflowIds.TryGetValue(value.Source, out var workflow)) throw new InvalidOperationException("Unknown workflow reference.");
                return new JsonObject { ["kind"] = "local", ["name"] = workflow };
            case "input" or "output" or "expression" or "compute" or "confirmation" or "decision_binding" when allowReferences: return JsonValue.Create(ToExpression(value, scope));
            case "template" when allowReferences:
                var template = value.Text ?? "";
                EnsureUnique(value.Members.Select(m => m.Name), "template binding");
                foreach (var binding in value.Members)
                {
                    var marker = "{{" + binding.Name + "}}";
                    if (!template.Contains(marker, StringComparison.Ordinal)) throw new InvalidOperationException("A template binding has no matching placeholder.");
                    template = template.Replace(marker, ToExpression(binding.Value, scope), StringComparison.Ordinal);
                }
                if (TemplatePlaceholder().IsMatch(template)) throw new InvalidOperationException("A template placeholder has no declared binding.");
                return JsonValue.Create(PlanningExpressionBindings.Template(template, scope.NodeIds, scope.NodeTypes));
            default: throw new InvalidOperationException("Invalid or forbidden planning value kind.");
        }
    }

    private static string ToExpression(PlanningValue value, LoweringScope scope)
    {
        string expression;
        if (value.ResultChannel is not null && value.Kind != "output") throw new InvalidOperationException("Only output references can select a result channel.");
        if (value.Kind == "input")
        {
            if (value.Source is null || !scope.Inputs.Contains(value.Source)) throw new InvalidOperationException("Unknown input reference.");
            expression = "data.inputs" + Segment(value.Source) + string.Concat(value.Path.Select(Segment));
        }
        else if (value.Kind == "output")
        {
            if (value.Source is null || !scope.NodeIds.TryGetValue(value.Source, out var node)) throw new InvalidOperationException("Unknown producer reference.");
            var type = scope.NodeTypes[value.Source];
            if (value.ResultChannel is not (null or "default" or "structured")) throw new InvalidOperationException("Unknown result channel.");
            if (value.ResultChannel == "structured" && type is not ("mcp.call" or "llm.call"))
                throw new InvalidOperationException("This producer does not support the structured result channel.");
            var envelope = value.ResultChannel == "structured" ? ".json" : type switch { "workflow.call" => ".outputs", "mcp.call" => ".response", _ => "" };
            expression = "data.steps." + node + envelope + ResultPath(type, value.Path, scope);
        }
        else if (value.Kind == "decision_binding")
        {
            if (value.Items.Count != 1) throw new InvalidOperationException("Invalid locked decision binding.");
            expression = ExpressionBody(value.Items[0]);
        }
        else if (value.Kind == "confirmation")
        {
            if (value.Items.Count != 1 || string.IsNullOrEmpty(value.Text) || string.IsNullOrEmpty(value.Source)) throw new InvalidOperationException("Invalid confirmation mapping.");
            expression = GnOuGo.Flow.Core.Expressions.ConfirmationDecisionExpression.Build(ExpressionBody(value.Items[0]), value.Text, value.Source);
        }
        else if (value.Kind == "compute")
        {
            EnsureUnique(value.Members.Select(m => m.Name), "computation parameter");
            PlanningComputations.Validate(value);
            expression = "((" + string.Join(",", value.Members.Select(m => m.Name)) + ") => (" + PlanningComputations.Expression(value.Text) + "))(" + string.Join(",", value.Members.Select(m => ExpressionBody(m.Value))) + ")";
        }
        else if (value.Kind == "template")
        {
            var rendered = LowerValue(value, scope)!.GetValue<string>();
            var parts = new List<string>(); var start = 0;
            foreach (var segment in GnOuGo.Flow.Core.Expressions.ExpressionSegments.Read(rendered))
            {
                if (segment.Start > start) parts.Add(JsonSerializer.Serialize(rendered[start..segment.Start], PlanningJsonContext.Default.String));
                parts.Add("((v) => v == null ? '' : typeof v === 'string' ? v : JSON.stringify(v))(" + segment.Expression + ")");
                start = segment.Start + segment.Length;
            }
            if (start < rendered.Length) parts.Add(JsonSerializer.Serialize(rendered[start..], PlanningJsonContext.Default.String));
            expression = parts.Count == 0 ? "''" : "(" + string.Join(" + ", parts) + ")";
        }
        else if (value.Kind == "expression")
        {
            expression = value.Text ?? throw new InvalidOperationException("An expression must have text.");
            if (expression.StartsWith("${", StringComparison.Ordinal) && expression.EndsWith('}')) expression = expression[2..^1];
            expression = PlanningExpressionBindings.Expression(expression, scope.NodeIds, scope.NodeTypes);
        }
        else if (value.Kind == "object")
        {
            EnsureUnique(value.Members.Select(m => m.Name), "member");
            expression = "({" + string.Join(",", value.Members.Select(m => JsonSerializer.Serialize(m.Name, PlanningJsonContext.Default.String) + ":" + ExpressionBody(m.Value))) + "})";
        }
        else if (value.Kind == "array") expression = "[" + string.Join(",", value.Items.Select(ExpressionBody)) + "]";
        else
        {
            var literal = LowerValue(value, scope, allowReferences: false);
            expression = literal?.ToJsonString() ?? "null";
        }
        return "${" + expression + "}";

        string ExpressionBody(PlanningValue member)
        {
            var resolved = ToExpression(member, scope);
            return resolved[2..^1];
        }
    }

    private static string Segment(string value)
    {
        return Identifier().IsMatch(value) ? "." + value : "[" + JsonSerializer.Serialize(value, PlanningJsonContext.Default.String) + "]";
    }

    private static string ResultPath(string type, IReadOnlyList<string> path, LoweringScope scope)
    {
        if (path.Count == 0) return "";
        var childIndex = type is "switch" or "sequence" ? 0 : type is "loop.sequential" or "loop.parallel" && path[0] == "results" && path.Count >= 3 ? 2 : -1;
        if (childIndex >= 0 && scope.NodeIds.TryGetValue(path[childIndex], out var child))
            return string.Concat(path.Take(childIndex).Select(Segment)) + Segment(child) + ResultPath(scope.NodeTypes[path[childIndex]], path.Skip(childIndex + 1).ToArray(), scope);
        return string.Concat(path.Select(Segment));
    }

    public static JsonObject ToJsonSchema(PlanningSchema schema, PlanningPreparation preparation, int depth = 0)
    {
        if (depth > 32) throw new InvalidOperationException("Schema nesting exceeds 32 levels.");
        if (schema.CapabilityId is not null) return PlanningSchemaReferences.Resolve(schema, preparation);
        if (schema.SchemaPointer is not null) throw new InvalidOperationException("A schemaPointer requires a capabilityId.");
        if (schema.Type is not ("string" or "number" or "integer" or "boolean" or "object" or "array"))
            throw new InvalidOperationException("Planning ports require a concrete type.");
        var result = new JsonObject { ["type"] = schema.Nullable ? new JsonArray(schema.Type, "null") : JsonValue.Create(schema.Type) };
        if (schema.Description is not null) result["description"] = schema.Description;
        if (schema.Enum.Count > 0) result["enum"] = new JsonArray(schema.Enum.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        if (schema.Type == "array") result["items"] = ToJsonSchema(schema.Items ?? throw new InvalidOperationException("An array schema requires items."), preparation, depth + 1);
        if (schema.Type == "object")
        {
            EnsureUnique(schema.Properties.Select(p => p.Name), "schema property");
            if (schema.Properties.Count == 0 && schema.AdditionalProperties is null) throw new InvalidOperationException("An object schema requires typed properties or typed additional properties.");
            var properties = new JsonObject();
            foreach (var property in schema.Properties) properties[property.Name] = ToJsonSchema(property.Schema, preparation, depth + 1);
            result["properties"] = properties;
            result["required"] = new JsonArray(schema.Properties.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray());
            result["additionalProperties"] = schema.AdditionalProperties is null ? JsonValue.Create(false) : ToJsonSchema(schema.AdditionalProperties, preparation, depth + 1);
        }
        return result;
    }

    private static JsonObject LowerSchema(PlanningSchema schema, PlanningPreparation preparation) => ToFlowSchema(ToJsonSchema(schema, preparation));

    internal static JsonObject ToFlowSchema(JsonObject schema)
    {
        string[] supported = ["type", "description", "enum", "items", "properties", "required", "additionalProperties", "title", "$schema"];
        if (schema.Any(field => !supported.Contains(field.Key, StringComparer.Ordinal)))
            throw new InvalidOperationException("The authoritative schema contains constraints that cannot be preserved in a Flow port. Select a supported schema property or retain it as a step output schema.");
        var type = schema["type"];
        if (type is JsonArray union && union.Count(t => t?.GetValue<string>() != "null") != 1)
            throw new InvalidOperationException("A Flow port cannot represent this union schema without losing constraints.");
        var nullable = type is JsonArray types && types.Any(t => t?.GetValue<string>() == "null");
        var name = type is JsonArray ts ? ts.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t != "null") : type?.GetValue<string>();
        if (name is null) throw new InvalidOperationException("The schema cannot be represented by a Flow port type.");
        var result = new JsonObject { ["type"] = name };
        if (nullable) result["nullable"] = true;
        foreach (var field in new[] { "description", "enum" }) if (schema[field] is { } value) result[field] = value.DeepClone();
        if (schema["items"] is JsonObject items) result["items"] = ToFlowSchema(items);
        if (schema["properties"] is JsonObject properties)
        {
            var fields = new JsonObject();
            foreach (var (key, value) in properties) fields[key] = ToFlowSchema(value as JsonObject ?? throw new InvalidOperationException("Invalid property schema."));
            result["properties"] = fields;
        }
        if (schema["required"] is JsonArray required) result["required_properties"] = required.DeepClone();
        if (schema["additionalProperties"] is JsonObject additional) result["additional_properties"] = ToFlowSchema(additional);
        return result;
    }

    private static YamlNode ToYaml(JsonNode? value) => value switch
    {
        JsonObject obj => new YamlMappingNode(obj.Select(p => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(p.Key), ToYaml(p.Value)))),
        JsonArray array => new YamlSequenceNode(array.Select(ToYaml)),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => new YamlScalarNode(text) { Style = text.Contains('\n') ? ScalarStyle.Literal : ScalarStyle.DoubleQuoted },
        null => new YamlScalarNode("null") { Style = ScalarStyle.Plain },
        _ => new YamlScalarNode(value.ToJsonString()) { Style = ScalarStyle.Plain }
    };

    private static void EnsureUnique(IEnumerable<string> values, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) throw new InvalidOperationException($"Empty or duplicate {kind} key.");
    }

    private sealed record LoweringScope(PlanningPreparation Preparation, Dictionary<string, string> NodeIds, Dictionary<string, string> WorkflowIds, HashSet<string> Inputs, Dictionary<string, string> NodeTypes);
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"\{\{[^{}]+\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplatePlaceholder();
}
