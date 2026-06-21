using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Stable declarative contract owned by a step executor. Schemas use JSON Schema vocabulary.
/// Mutual-exclusion groups cover executor rules that are awkward to express with a simple
/// object schema while keeping diagnostics precise.
/// </summary>
public sealed record StepContract(
    JsonObject InputSchema,
    JsonObject OutputSchema,
    bool InputRequired = false,
    IReadOnlyList<IReadOnlyList<string>>? MutuallyExclusiveInputFields = null);

public sealed record StepContractViolation(string Field, string Message);

/// <summary>Contracts for the executors registered by <see cref="WorkflowEngine"/>.</summary>
public static class BuiltInStepContracts
{
    private static readonly IReadOnlyDictionary<string, StepContract> Contracts =
        new Dictionary<string, StepContract>(StringComparer.Ordinal)
        {
            ["sequence"] = Contract(ClosedObject(), OpenObject()),
            ["parallel"] = Contract(
                Object(("max_concurrency", PositiveInteger())),
                Object(("branches", Array(Any())))),
            ["loop.sequential"] = Contract(
                Object(
                    ("items", Array(Any())),
                    ("over", Array(Any())),
                    ("times", NonNegativeInteger()),
                    ("while", Boolean()),
                    ("max_times", PositiveInteger())),
                Object(("results", Array(Any())), ("count", Integer())),
                mutuallyExclusive: new[] { Group("items", "over", "times") }),
            ["loop.parallel"] = Contract(
                Object(new[] { "items" }, ("items", Array(Any())), ("max_concurrency", PositiveInteger())),
                Object(("results", Array(Any())), ("count", Integer())),
                inputRequired: true),
            ["switch"] = Contract(ClosedObject(), OpenObject()),
            ["set"] = Contract(OpenObject(), OpenObject(), inputRequired: true),
            ["template.render"] = Contract(
                Object(new[] { "template" },
                    ("engine", Enum("mustache")),
                    ("template", String()),
                    ("data", Any()),
                    ("mode", Enum("text", "json")),
                    ("strict", Boolean())),
                Object(("text", String()), ("json", Any()), ("meta", Object(("engine", String())))),
                inputRequired: true),
            ["llm.call"] = Contract(
                Object(new[] { "prompt" },
                    ("provider", String()),
                    ("model", String()),
                    ("prompt", String()),
                    ("system", String()),
                    ("temperature", Number()),
                    ("reasoning", Enum("auto", "minimal", "low", "medium", "high", "max")),
                    ("structured_output", StructuredOutput()),
                    ("max_tokens", PositiveInteger())),
                Object(
                    ("text", String()),
                    ("json", Any()),
                    ("usage", OpenObject()),
                    ("meta", Object(("model", String()))),
                    ("raw", Any())),
                inputRequired: true),
            ["workflow.call"] = Contract(
                Object(new[] { "ref" }, ("ref", WorkflowReference()), ("args", Any())),
                Object(("outputs", Any()), ("workflow", String()), ("run", RunSummary())),
                inputRequired: true),
            ["workflow.route"] = Contract(
                WorkflowRouteInput(),
                Object(("selected", Array(Any())), ("results", Array(Any())), ("answer", String()), ("text", String())),
                inputRequired: true),
            ["workflow.plan"] = Contract(
                WorkflowPlanInput(),
                Object(("workflow", OpenObject()), ("yaml", String()), ("meta", OpenObject()), ("diagnostics", Array(Any()))),
                inputRequired: true),
            ["workflow.execute"] = Contract(
                Object(new[] { "from_step" }, ("from_step", String()), ("args", Any())),
                Object(("outputs", Any()), ("workflow", String()), ("run", RunSummary())),
                inputRequired: true),
            ["mcp.call"] = Contract(
                McpCallInput(),
                Object(
                    ("status", String()), ("response", Any()), ("error", OpenObject()),
                    ("correlation_id", String()), ("trace_id", String()), ("results", Array(Any())),
                    ("selection_mode", String()), ("text", String()), ("selection_text", String()),
                    ("tool_calls", Array(Any())), ("json", Any()), ("description", String()), ("messages", Array(Any()))),
                inputRequired: true,
                mutuallyExclusive: new[] { Group("method", "methods"), Group("request", "request_template") }),
            ["mcp.list"] = Contract(
                Object(new[] { "servers" },
                    ("servers", Array(String())),
                    ("include", Array(Enum("tools", "resources", "prompts"))),
                    ("timeout_ms", PositiveInteger())),
                Object(
                    ("status", String()), ("text", String()), ("servers", Array(Any())),
                    ("tools", Array(Any())), ("resources", Array(Any())), ("prompts", Array(Any()))),
                inputRequired: true),
            ["emit"] = Contract(
                Object(new[] { "message" }, ("message", String()), ("level", Enum("thinking", "info", "progress", "response"))),
                Object(("message", String()), ("level", String())),
                inputRequired: true),
            ["human.input"] = Contract(HumanInput(), OpenObject(), inputRequired: true)
        };

    public static StepContract? Get(string stepType) =>
        Contracts.TryGetValue(stepType, out var contract) ? contract : null;

    public static IReadOnlyDictionary<string, StepContract> All => Contracts;

    private static StepContract Contract(
        JsonObject input,
        JsonObject output,
        bool inputRequired = false,
        IReadOnlyList<IReadOnlyList<string>>? mutuallyExclusive = null) =>
        new(input, output, inputRequired, mutuallyExclusive);

    private static IReadOnlyList<string> Group(params string[] fields) => fields;

    private static JsonObject WorkflowRouteInput() => Object(
        new[] { "candidates" },
        ("prompt", String()),
        ("task", String()),
        ("query", String()),
        ("history", Any()),
        ("candidates", Array(Object(new[] { "ref" },
            ("ref", WorkflowReference()),
            ("description", String()),
            ("tags", Array(String())),
            ("tags_any", Array(String())),
            ("tags_all", Array(String())),
            ("exclude_tags", Array(String())),
            ("limit", PositiveInteger()),
            ("inputs", Any()),
            ("outputs", Any())))),
        ("selection", Object(
            ("mode", Enum("single", "multiple")),
            ("min", NonNegativeInteger()),
            ("max", PositiveInteger()),
            ("provider", String()),
            ("model", String()),
            ("temperature", Number()))),
        ("args", Object(
            ("passthrough", Boolean()),
            ("auto_extract", AnyOf(
                Boolean(),
                Object(
                    ("enabled", Boolean()),
                    ("provider", String()),
                    ("model", String()),
                    ("temperature", Number())))),
            ("add", OpenObject()))),
        ("execution", Object(("parallel", Boolean()), ("max_concurrency", PositiveInteger()))),
        ("combine", Object(
            ("strategy", Enum("synthesize", "first", "raw")),
            ("provider", String()),
            ("model", String()),
            ("temperature", Number()))));

    private static JsonObject WorkflowPlanInput() => Object(
        ("mode", Enum("pipeline")),
        ("raw_prompt", String()),
        ("name", String()),
        ("workflow_name", String()),
        ("document_name", String()),
        ("description", String()),
        ("generator", Object(
            ("mode", Enum("pipeline")),
            ("provider", String()),
            ("model", String()),
            ("instruction", String()),
            ("context", String()),
            ("raw_prompt", String()),
            ("name", String()),
            ("workflow_name", String()),
            ("document_name", String()),
            ("description", String()),
            ("reasoning", Enum("auto", "minimal", "low", "medium", "high", "max")),
            ("pipeline_leaf_name", String()),
            ("prefilter", AnyOf(
                Boolean(),
                Object(("provider", String()), ("model", String()), ("temperature", Number())))),
            ("skill", OpenObject()),
            ("inputs", OpenObject()),
            ("outputs", OpenObject()))),
        ("policy", Object(
            ("allowed_step_types", Array(String())),
            ("denied_step_types", Array(String())),
            ("allow_remote_workflow_refs", Boolean()))),
        ("limits", Object(("max_steps_total", PositiveInteger()))),
        ("validate", Object(("compile", Boolean()), ("dry_run", Boolean()))),
        ("on_invalid", Object(("action", Enum("fail", "stop", "reprompt")), ("max_attempts", PositiveInteger()))),
        ("skill", OpenObject()),
        ("inputs", OpenObject()),
        ("outputs", OpenObject()));

    private static JsonObject McpCallInput() => Object(
        new[] { "server" },
        ("server", String()),
        ("kind", Enum("tool", "prompt")),
        ("method", String()),
        ("methods", Array(String())),
        ("request", Any()),
        ("request_template", String()),
        ("template_data", OpenObject()),
        ("timeout_ms", PositiveInteger()),
        ("prompt", String()),
        ("provider", String()),
        ("model", String()),
        ("temperature", Number()),
        ("tools", Array(Any())),
        ("prompts", Array(Any())),
        ("structured_output", StructuredOutput()),
        ("raise_on_error", Boolean()),
        ("raiseOnError", Boolean()),
        ("error_policy", Object(("detect_result_errors", Boolean()), ("detectResultErrors", Boolean()))),
        ("detect_result_errors", Boolean()),
        ("detectResultErrors", Boolean()));

    private static JsonObject HumanInput() => Object(
        new[] { "prompt" },
        ("prompt", String()),
        ("mode", Enum("text", "choice", "confirm", "form")),
        ("timeout_ms", PositiveInteger()),
        ("choices", Array(Any())),
        ("fields", Array(Object(new[] { "name" },
            ("name", String()),
            ("type", String()),
            ("label", String()),
            ("description", String()),
            ("required", Boolean()),
            ("default", Any()),
            ("options", Array(Any()))))),
        ("context", Any()));

    private static JsonObject WorkflowReference() => Object(
        ("kind", String()),
        ("name", String()),
        ("agent", String()),
        ("url", String()),
        ("path", String()),
        ("integrity", String()),
        ("export", String()));

    private static JsonObject StructuredOutput() => Object(
        ("schema_inline", Any()),
        ("schema_ref", Any()),
        ("strict", Boolean()));

    private static JsonObject RunSummary() =>
        Object(("steps_executed", Integer()), ("success", Boolean()));

    private static JsonObject Object(params (string Name, JsonNode Schema)[] properties) =>
        Object(System.Array.Empty<string>(), properties);

    private static JsonObject Object(
        IReadOnlyList<string> required,
        params (string Name, JsonNode Schema)[] properties)
    {
        var propertyObject = new JsonObject();
        foreach (var (name, schema) in properties)
            propertyObject[name] = schema;

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertyObject,
            ["additionalProperties"] = false
        };
        if (required.Count > 0)
            result["required"] = new JsonArray(required.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return result;
    }

    private static JsonObject ClosedObject() => Object();
    private static JsonObject OpenObject() => new() { ["type"] = "object", ["additionalProperties"] = true };
    private static JsonObject Any() => new() { ["x-gnougo-opaque"] = true };
    private static JsonObject String() => new() { ["type"] = "string" };
    private static JsonObject Number() => new() { ["type"] = "number" };
    private static JsonObject Integer() => new() { ["type"] = "integer" };
    private static JsonObject NonNegativeInteger() => new() { ["type"] = "integer", ["minimum"] = 0 };
    private static JsonObject PositiveInteger() => new() { ["type"] = "integer", ["minimum"] = 1 };
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject AnyOf(params JsonNode[] schemas) => new()
    {
        ["anyOf"] = new JsonArray(schemas)
    };

    private static JsonObject Enum(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray())
    };
}

internal static class StepContractValidator
{
    public static IReadOnlyList<StepContractViolation> ValidateInput(JsonNode? input, StepContract contract)
    {
        var violations = new List<StepContractViolation>();
        if (input == null)
        {
            if (contract.InputRequired)
                violations.Add(new StepContractViolation("input", "input is required and must be an object"));
            return violations;
        }

        ValidateNode(input, contract.InputSchema, "input", violations);

        if (input is JsonObject obj && contract.MutuallyExclusiveInputFields != null)
        {
            foreach (var group in contract.MutuallyExclusiveInputFields)
            {
                var present = group.Where(obj.ContainsKey).ToArray();
                if (present.Length > 1)
                {
                    violations.Add(new StepContractViolation(
                        "input." + present[1],
                        $"fields {string.Join(", ", present.Select(static field => $"'{field}'"))} are mutually exclusive"));
                }
            }
        }

        return violations;
    }

    private static void ValidateNode(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<StepContractViolation> violations)
    {
        if (IsDynamic(value) || IsOpaque(schema))
            return;

        if (schema["anyOf"] is JsonArray variants)
        {
            var candidates = variants
                .OfType<JsonObject>()
                .Where(candidate => MatchesType(value, candidate["type"]?.GetValue<string>()))
                .ToArray();
            if (candidates.Length == 0)
            {
                var expected = variants
                    .OfType<JsonObject>()
                    .Select(candidate => candidate["type"]?.GetValue<string>())
                    .Where(static type => type != null)
                    .Distinct(StringComparer.Ordinal);
                violations.Add(new StepContractViolation(path, $"expected {string.Join(" or ", expected)}, got {DescribeType(value)}"));
                return;
            }

            ValidateNode(value, candidates[0], path, violations);
            return;
        }

        var type = schema["type"]?.GetValue<string>();
        if (!MatchesType(value, type))
        {
            violations.Add(new StepContractViolation(path, $"expected {type ?? "a valid value"}, got {DescribeType(value)}"));
            return;
        }

        if (schema["enum"] is JsonArray allowed && value is JsonValue scalar)
        {
            var matches = allowed.Any(candidate => JsonNode.DeepEquals(candidate, scalar));
            if (!matches)
                violations.Add(new StepContractViolation(path, $"value must be one of: {string.Join(", ", allowed.Select(static item => item?.ToJsonString()))}"));
        }

        if (value is JsonObject obj && string.Equals(type, "object", StringComparison.Ordinal))
        {
            if (schema["required"] is JsonArray required)
            {
                foreach (var requiredNode in required)
                {
                    var name = requiredNode?.GetValue<string>();
                    if (name != null && !obj.ContainsKey(name))
                        violations.Add(new StepContractViolation($"{path}.{name}", $"required field '{name}' is missing"));
                }
            }

            var properties = schema["properties"] as JsonObject;
            var additionalAllowed = schema["additionalProperties"]?.GetValue<bool>() ?? true;
            foreach (var (name, child) in obj)
            {
                if (properties != null && properties[name] is JsonObject childSchema)
                {
                    ValidateNode(child, childSchema, $"{path}.{name}", violations);
                    continue;
                }

                if (!additionalAllowed)
                    violations.Add(new StepContractViolation($"{path}.{name}", $"unknown field '{name}'"));
            }
        }
        else if (value is JsonArray array && string.Equals(type, "array", StringComparison.Ordinal)
                 && schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < array.Count; i++)
                ValidateNode(array[i], itemSchema, $"{path}[{i}]", violations);
        }
    }

    private static bool IsDynamic(JsonNode? value) =>
        value is JsonValue scalar
        && scalar.TryGetValue<string>(out var text)
        && text?.Contains("${", StringComparison.Ordinal) == true;

    private static bool IsOpaque(JsonObject schema) =>
        schema["x-gnougo-opaque"] is JsonValue value
        && value.TryGetValue<bool>(out var opaque)
        && opaque;

    private static bool MatchesType(JsonNode? value, string? type)
    {
        if (type == null)
            return true;
        if (value == null)
            return string.Equals(type, "null", StringComparison.Ordinal);

        return type switch
        {
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue scalar && scalar.TryGetValue<string>(out _),
            "boolean" => value is JsonValue scalar && scalar.TryGetValue<bool>(out _),
            "integer" => IsInteger(value),
            "number" => IsNumber(value),
            _ => true
        };
    }

    private static bool IsNumber(JsonNode value) =>
        value is JsonValue scalar
        && (scalar.TryGetValue<decimal>(out _) || scalar.TryGetValue<double>(out _)
            || scalar.TryGetValue<long>(out _) || scalar.TryGetValue<int>(out _));

    private static bool IsInteger(JsonNode value) =>
        value is JsonValue scalar
        && (scalar.TryGetValue<long>(out _) || scalar.TryGetValue<int>(out _)
            || (scalar.TryGetValue<decimal>(out var number) && decimal.Truncate(number) == number));

    private static string DescribeType(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue scalar when scalar.TryGetValue<string>(out _) => "string",
        JsonValue scalar when scalar.TryGetValue<bool>(out _) => "boolean",
        JsonValue scalar when IsInteger(scalar) => "integer",
        JsonValue => "number",
        _ => "value"
    };
}
