using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed record McpToolOutputContract(
    string ServerName,
    string ToolName,
    JsonNode? OutputSchema,
    JsonNode? ExampleResponse);

internal sealed class WorkflowSemanticValidationError
{
    public string Code { get; init; } = "SEMANTIC_MAPPING_ERROR";
    public string? WorkflowName { get; init; }
    public string? StepId { get; init; }
    public string Field { get; init; } = "";
    public string InvalidPath { get; init; } = "";
    public IReadOnlyList<string> AllowedPaths { get; init; } = Array.Empty<string>();
    public string Suggestion { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class WorkflowSemanticValidationException : Exception
{
    public IReadOnlyList<WorkflowSemanticValidationError> Errors { get; }

    public WorkflowSemanticValidationException(IReadOnlyList<WorkflowSemanticValidationError> errors)
        : base(WorkflowPlanSemanticValidator.FormatErrors(errors))
    {
        Errors = errors;
    }
}

/// <summary>
/// Performs static semantic checks that are specific to generated plans.
/// It validates references to previous step outputs such as
/// <c>${data.steps.fetch.response.title}</c> against known step output contracts.
/// </summary>
internal static class WorkflowPlanSemanticValidator
{
    private static readonly Regex ExpressionRegex = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);
    private static readonly Regex DataStepsPathRegex = new(
        @"\bdata\.steps\.([A-Za-z_][A-Za-z0-9_-]*)(?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)",
        RegexOptions.Compiled);

    public static void Validate(WorkflowDocument document, IReadOnlyList<McpToolOutputContract>? mcpToolContracts = null)
    {
        var errors = new List<WorkflowSemanticValidationError>();
        var mcpContracts = BuildMcpContractLookup(mcpToolContracts);

        foreach (var (workflowName, workflow) in document.Workflows)
        {
            var allStepIds = CollectStepIds(workflow.Steps).ToHashSet(StringComparer.Ordinal);
            var knownContracts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            ValidateStepList(workflow.Steps, workflowName, knownContracts, allStepIds, mcpContracts, errors);

            if (workflow.Outputs != null)
            {
                foreach (var (outputName, outputDef) in workflow.Outputs)
                    ValidateOutputDef(outputDef, workflowName, $"outputs.{outputName}", knownContracts, allStepIds, errors);
            }
        }

        if (errors.Count > 0)
            throw new WorkflowSemanticValidationException(errors);
    }

    internal static string FormatErrors(IReadOnlyList<WorkflowSemanticValidationError> errors)
    {
        var root = new JsonObject
        {
            ["error"] = "Generated workflow semantic validation failed",
            ["errors"] = new JsonArray(errors.Select(error => (JsonNode)new JsonObject
            {
                ["code"] = error.Code,
                ["workflow"] = error.WorkflowName,
                ["step"] = error.StepId,
                ["field"] = error.Field,
                ["invalid_path"] = error.InvalidPath,
                ["allowed_paths"] = new JsonArray(error.AllowedPaths.Select(path => (JsonNode)JsonValue.Create(path)).ToArray()),
                ["suggestion"] = error.Suggestion,
                ["message"] = error.Message
            }).ToArray())
        };

        return root.ToJsonString();
    }

    private static Dictionary<(string ServerName, string ToolName), McpToolOutputContract> BuildMcpContractLookup(IReadOnlyList<McpToolOutputContract>? contracts)
    {
        var lookup = new Dictionary<(string ServerName, string ToolName), McpToolOutputContract>();
        if (contracts == null)
            return lookup;

        foreach (var contract in contracts)
            lookup[(contract.ServerName, contract.ToolName)] = contract;
        return lookup;
    }

    private static IEnumerable<string> CollectStepIds(IEnumerable<StepDef> steps)
    {
        foreach (var step in steps)
        {
            if (!string.IsNullOrWhiteSpace(step.Id))
                yield return step.Id;

            if (step.Steps != null)
                foreach (var id in CollectStepIds(step.Steps))
                    yield return id;

            if (step.Branches != null)
                foreach (var id in step.Branches.SelectMany(branch => CollectStepIds(branch.Steps)))
                    yield return id;

            if (step.Cases != null)
                foreach (var id in step.Cases.SelectMany(@case => CollectStepIds(@case.Steps)))
                    yield return id;

            if (step.Default != null)
                foreach (var id in CollectStepIds(step.Default))
                    yield return id;
        }
    }

    private static void ValidateStepList(
        IReadOnlyList<StepDef> steps,
        string workflowName,
        Dictionary<string, JsonNode?> knownContracts,
        HashSet<string> allStepIds,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        foreach (var step in steps)
            ValidateStep(step, workflowName, knownContracts, allStepIds, mcpContracts, errors);
    }

    private static void ValidateStep(
        StepDef step,
        string workflowName,
        Dictionary<string, JsonNode?> knownContracts,
        HashSet<string> allStepIds,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        ValidateString(step.If, workflowName, step.Id, "if", knownContracts, allStepIds, errors);
        ValidateString(step.Expr, workflowName, step.Id, "expr", knownContracts, allStepIds, errors);
        ValidateJson(step.Input, workflowName, step.Id, "input", knownContracts, allStepIds, errors);

        if (step.OnError != null)
        {
            for (var i = 0; i < step.OnError.Cases.Count; i++)
            {
                var onErrorCase = step.OnError.Cases[i];
                ValidateString(onErrorCase.If, workflowName, step.Id, $"on_error.cases[{i}].if", knownContracts, allStepIds, errors);
                ValidateJson(onErrorCase.SetOutput, workflowName, step.Id, $"on_error.cases[{i}].set_output", knownContracts, allStepIds, errors);
            }
        }

        if (step.Type == "parallel" && step.Branches != null)
        {
            var branchProducedContracts = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var branch in step.Branches)
            {
                var branchKnown = CloneContracts(knownContracts);
                ValidateStepList(branch.Steps, workflowName, branchKnown, allStepIds, mcpContracts, errors);
                foreach (var produced in branchKnown.Where(kv => !knownContracts.ContainsKey(kv.Key)))
                    branchProducedContracts[produced.Key] = produced.Value?.DeepClone();
            }

            foreach (var produced in branchProducedContracts)
                knownContracts[produced.Key] = produced.Value?.DeepClone();
        }
        else
        {
            if (step.Steps != null)
                ValidateStepList(step.Steps, workflowName, knownContracts, allStepIds, mcpContracts, errors);

            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                {
                    ValidateString(@case.When, workflowName, step.Id, "cases.when", knownContracts, allStepIds, errors);
                    ValidateStepList(@case.Steps, workflowName, knownContracts, allStepIds, mcpContracts, errors);
                }
            }

            if (step.Default != null)
                ValidateStepList(step.Default, workflowName, knownContracts, allStepIds, mcpContracts, errors);
        }

        if (!string.IsNullOrWhiteSpace(step.Id))
            knownContracts[step.Id] = BuildStepOutputSchema(step, mcpContracts);
    }

    private static Dictionary<string, JsonNode?> CloneContracts(Dictionary<string, JsonNode?> source)
    {
        var clone = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
            clone[key] = value?.DeepClone();
        return clone;
    }

    private static void ValidateOutputDef(
        OutputDef outputDef,
        string workflowName,
        string field,
        Dictionary<string, JsonNode?> knownContracts,
        HashSet<string> allStepIds,
        List<WorkflowSemanticValidationError> errors)
    {
        ValidateString(outputDef.Expr, workflowName, null, field, knownContracts, allStepIds, errors);

        if (outputDef.Properties != null)
        {
            foreach (var (propertyName, propertyDef) in outputDef.Properties)
                ValidateOutputDef(propertyDef, workflowName, $"{field}.properties.{propertyName}", knownContracts, allStepIds, errors);
        }

        if (outputDef.Items != null)
            ValidateOutputDef(outputDef.Items, workflowName, $"{field}.items", knownContracts, allStepIds, errors);

        if (outputDef.AdditionalProperties != null)
            ValidateOutputDef(outputDef.AdditionalProperties, workflowName, $"{field}.additional_properties", knownContracts, allStepIds, errors);
    }

    private static void ValidateJson(
        JsonNode? node,
        string workflowName,
        string? stepId,
        string field,
        Dictionary<string, JsonNode?> knownContracts,
        HashSet<string> allStepIds,
        List<WorkflowSemanticValidationError> errors)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                ValidateString(text, workflowName, stepId, field, knownContracts, allStepIds, errors);
                break;
            case JsonObject obj:
                foreach (var (key, child) in obj)
                    ValidateJson(child, workflowName, stepId, $"{field}.{key}", knownContracts, allStepIds, errors);
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    ValidateJson(array[i], workflowName, stepId, $"{field}[{i}]", knownContracts, allStepIds, errors);
                break;
        }
    }

    private static void ValidateString(
        string? text,
        string workflowName,
        string? stepId,
        string field,
        Dictionary<string, JsonNode?> knownContracts,
        HashSet<string> allStepIds,
        List<WorkflowSemanticValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("data.steps.", StringComparison.Ordinal))
            return;

        foreach (Match expressionMatch in ExpressionRegex.Matches(text))
        {
            var expression = expressionMatch.Groups[1].Value;
            foreach (Match pathMatch in DataStepsPathRegex.Matches(expression))
            {
                var referencedStepId = pathMatch.Groups[1].Value;
                var propertyPath = SplitPath(pathMatch.Groups["path"].Value);
                var invalidPath = "data.steps." + referencedStepId + (propertyPath.Count == 0 ? "" : "." + string.Join('.', propertyPath));

                if (!knownContracts.TryGetValue(referencedStepId, out var schema))
                {
                    var existsLater = allStepIds.Contains(referencedStepId);
                    errors.Add(new WorkflowSemanticValidationError
                    {
                        Code = existsLater ? "STEP_REFERENCE_NOT_AVAILABLE" : "STEP_REFERENCE_UNKNOWN",
                        WorkflowName = workflowName,
                        StepId = stepId,
                        Field = field,
                        InvalidPath = invalidPath,
                        AllowedPaths = knownContracts.Keys.OrderBy(x => x, StringComparer.Ordinal).Select(id => "data.steps." + id).ToArray(),
                        Suggestion = existsLater
                            ? $"Move this reference after step '{referencedStepId}' has executed, or move the producing step earlier."
                            : "Reference an existing previous step id, or add the missing producing step before this expression.",
                        Message = existsLater
                            ? $"Step '{referencedStepId}' exists but is not available at this point in execution."
                            : $"Step '{referencedStepId}' does not exist in this workflow."
                    });
                    continue;
                }

                var validation = ValidateSchemaPath(schema, propertyPath);
                if (validation.IsValid)
                    continue;

                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = validation.IsOpaqueResponse ? "OPAQUE_RESPONSE_DEEP_ACCESS" : "STEP_OUTPUT_PROPERTY_UNKNOWN",
                    WorkflowName = workflowName,
                    StepId = stepId,
                    Field = field,
                    InvalidPath = invalidPath,
                    AllowedPaths = EnumerateAllowedPaths("data.steps." + referencedStepId, schema).ToArray(),
                    Suggestion = validation.IsOpaqueResponse
                        ? $"Do not invent fields under '{invalidPath[..invalidPath.IndexOf(".response", StringComparison.Ordinal)]}.response'. Use json(data.steps.{referencedStepId}.response), or add an llm.call normalization step with structured_output before accessing named fields."
                        : $"Use one of the allowed paths for step '{referencedStepId}', or add a normalization step that produces the desired property with structured_output.",
                    Message = validation.Message
                });
            }
        }
    }

    private static IReadOnlyList<string> SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<string>();

        return path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private sealed record SchemaPathValidationResult(bool IsValid, string Message, bool IsOpaqueResponse = false);

    private static SchemaPathValidationResult ValidateSchemaPath(JsonNode? schema, IReadOnlyList<string> path)
    {
        if (path.Count == 0)
            return new SchemaPathValidationResult(true, "");

        if (schema is not JsonObject schemaObject)
            return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' is opaque and has no known object schema.");

        if (IsOpaqueSchema(schemaObject))
            return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' crosses an opaque value with no known schema.", IsOpaqueResponse: true);

        if (TryGetString(schemaObject, "type", out var type) && type == "array")
            return new SchemaPathValidationResult(false, $"Output path '{string.Join('.', path)}' tries to read object properties from an array output.");

        var properties = schemaObject["properties"] as JsonObject;
        var segment = path[0];
        if (properties == null || !properties.TryGetPropertyValue(segment, out var childSchema) || childSchema == null)
        {
            if (AllowsAdditionalProperties(schemaObject))
                return new SchemaPathValidationResult(true, "");

            return new SchemaPathValidationResult(false, $"Property '{segment}' is not defined by the output schema.");
        }

        if (path.Count == 1)
            return new SchemaPathValidationResult(true, "");

        return ValidateSchemaPath(childSchema, path.Skip(1).ToArray());
    }

    private static bool AllowsAdditionalProperties(JsonObject schemaObject)
    {
        if (!schemaObject.TryGetPropertyValue("additionalProperties", out var additional) || additional == null)
            return false;

        if (additional is JsonValue value && value.TryGetValue<bool>(out var allowed))
            return allowed;

        return additional is JsonObject;
    }

    private static bool IsOpaqueSchema(JsonObject schemaObject)
    {
        return schemaObject.TryGetPropertyValue("x-gnougo-opaque", out var opaqueNode)
            && opaqueNode is JsonValue opaqueValue
            && opaqueValue.TryGetValue<bool>(out var opaque)
            && opaque;
    }

    private static IEnumerable<string> EnumerateAllowedPaths(string prefix, JsonNode? schema, int depth = 0)
    {
        yield return prefix;

        if (depth >= 4 || schema is not JsonObject schemaObject || IsOpaqueSchema(schemaObject))
            yield break;

        if (schemaObject["properties"] is not JsonObject properties)
            yield break;

        foreach (var (propertyName, childSchema) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var childPrefix = prefix + "." + propertyName;
            yield return childPrefix;

            if (childSchema is JsonObject childObject && !IsOpaqueSchema(childObject))
            {
                foreach (var nestedPath in EnumerateAllowedPaths(childPrefix, childObject, depth + 1).Skip(1))
                    yield return nestedPath;
            }
        }
    }

    private static JsonNode? BuildStepOutputSchema(
        StepDef step,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts)
    {
        return step.Type switch
        {
            "set" => BuildSetOutputSchema(step),
            "template.render" => BuildTemplateRenderOutputSchema(step),
            "llm.call" => BuildLlmCallOutputSchema(step),
            "mcp.call" => BuildMcpCallOutputSchema(step, mcpContracts),
            "mcp.list" => ObjectSchema(("status", StringSchema()), ("text", StringSchema()), ("servers", ArraySchema()), ("tools", ArraySchema()), ("resources", ArraySchema()), ("prompts", ArraySchema())),
            "workflow.plan" => ObjectSchema(("workflow", ObjectSchema()), ("yaml", StringSchema()), ("meta", ObjectSchema()), ("diagnostics", ArraySchema())),
            "workflow.execute" => ObjectSchema(("outputs", OpaqueSchema()), ("workflow", StringSchema()), ("run", ObjectSchema(("steps_executed", NumberSchema()), ("success", BooleanSchema())))),
            "sequence" => ObjectSchema(("steps", ObjectSchema()), ("count", NumberSchema())),
            "parallel" => ObjectSchema(("branches", ArraySchema()), ("count", NumberSchema())),
            "loop.sequential" or "loop.parallel" => ObjectSchema(("results", ArraySchema()), ("count", NumberSchema())),
            "switch" => ObjectSchema(("matched", StringSchema()), ("steps", ObjectSchema())),
            "emit" => ObjectSchema(("event", OpaqueSchema()), ("status", StringSchema())),
            "human.input" => ObjectSchema(("value", OpaqueSchema()), ("text", StringSchema()), ("status", StringSchema())),
            _ => OpaqueSchema()
        };
    }

    private static JsonNode BuildSetOutputSchema(StepDef step)
    {
        if (step.Input is not JsonObject input)
            return ObjectSchema();

        var properties = new List<(string Name, JsonNode? Schema)>();
        foreach (var (key, value) in input)
            properties.Add((key, InferSchemaFromExample(value)));
        return ObjectSchema(properties.ToArray());
    }

    private static JsonNode BuildTemplateRenderOutputSchema(StepDef step)
    {
        var mode = TryGetInputString(step, "mode") ?? "text";
        return string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase)
            ? ObjectSchema(("json", OpaqueSchema()), ("meta", ObjectSchema(("engine", StringSchema()))))
            : ObjectSchema(("text", StringSchema()), ("meta", ObjectSchema(("engine", StringSchema()))));
    }

    private static JsonNode BuildLlmCallOutputSchema(StepDef step)
    {
        var jsonSchema = GetStructuredOutputSchema(step) ?? OpaqueSchema();
        return ObjectSchema(
            ("text", StringSchema()),
            ("json", jsonSchema),
            ("usage", ObjectSchema()),
            ("meta", ObjectSchema(("model", StringSchema()))),
            ("raw", OpaqueSchema()));
    }

    private static JsonNode BuildMcpCallOutputSchema(
        StepDef step,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts)
    {
        var input = step.Input as JsonObject;
        var kind = TryGetInputString(step, "kind") ?? "tool";
        var hasPromptSelection = input?.ContainsKey("prompt") == true;
        var structuredJsonSchema = GetStructuredOutputSchema(step);

        if (hasPromptSelection)
        {
            return ObjectSchema(
                ("status", StringSchema()),
                ("selection_mode", StringSchema()),
                ("text", StringSchema()),
                ("selection_text", StringSchema()),
                ("tool_calls", ArraySchema()),
                ("results", ArraySchema()),
                ("json", structuredJsonSchema ?? OpaqueSchema()));
        }

        if (string.Equals(kind, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            return ObjectSchema(
                ("status", StringSchema()),
                ("description", StringSchema()),
                ("messages", ArraySchema()),
                ("text", StringSchema()));
        }

        JsonNode responseSchema = OpaqueSchema();
        var serverName = TryGetInputString(step, "server");
        var methodName = TryGetInputString(step, "method");
        if (!string.IsNullOrWhiteSpace(serverName)
            && !string.IsNullOrWhiteSpace(methodName)
            && mcpContracts.TryGetValue((serverName, methodName), out var contract))
        {
            responseSchema = contract.OutputSchema?.DeepClone()
                ?? InferSchemaFromExample(contract.ExampleResponse)
                ?? OpaqueSchema();
        }

        return ObjectSchema(
            ("status", StringSchema()),
            ("response", responseSchema),
            ("error", ObjectSchema()),
            ("correlation_id", StringSchema()),
            ("trace_id", StringSchema()),
            ("results", ArraySchema()));
    }

    private static JsonNode? GetStructuredOutputSchema(StepDef step)
    {
        if (step.Input is not JsonObject input || input["structured_output"] is not JsonObject structuredOutput)
            return null;

        return structuredOutput["schema_inline"]?.DeepClone() ?? structuredOutput["schema_ref"]?.DeepClone();
    }

    private static string? TryGetInputString(StepDef step, string propertyName)
    {
        if (step.Input is not JsonObject input || !input.TryGetPropertyValue(propertyName, out var node) || node == null)
            return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && !text.Contains("${", StringComparison.Ordinal))
            return text;

        return null;
    }

    private static JsonNode? InferSchemaFromExample(JsonNode? example)
    {
        return example switch
        {
            JsonObject obj => InferObjectSchema(obj),
            JsonArray arr => new JsonObject
            {
                ["type"] = "array",
                ["items"] = arr.Count > 0 ? InferSchemaFromExample(arr[0]) : OpaqueSchema()
            },
            JsonValue value when value.TryGetValue<string>(out _) => StringSchema(),
            JsonValue value when value.TryGetValue<bool>(out _) => BooleanSchema(),
            JsonValue value when value.TryGetValue<int>(out _) => NumberSchema(),
            JsonValue value when value.TryGetValue<long>(out _) => NumberSchema(),
            JsonValue value when value.TryGetValue<double>(out _) => NumberSchema(),
            null => null,
            _ => OpaqueSchema()
        };
    }

    private static JsonNode InferObjectSchema(JsonObject obj)
    {
        var properties = new JsonObject();
        foreach (var (key, value) in obj)
            properties[key] = InferSchemaFromExample(value) ?? OpaqueSchema();

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
    }

    private static JsonObject ObjectSchema(params (string Name, JsonNode? Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
            props[name] = schema?.DeepClone() ?? OpaqueSchema();

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false
        };
    }

    private static JsonObject StringSchema() => new() { ["type"] = "string" };
    private static JsonObject NumberSchema() => new() { ["type"] = "number" };
    private static JsonObject BooleanSchema() => new() { ["type"] = "boolean" };
    private static JsonObject ArraySchema() => new() { ["type"] = "array", ["items"] = OpaqueSchema() };
    private static JsonObject OpaqueSchema() => new() { ["x-gnougo-opaque"] = true };

    private static bool TryGetString(JsonObject obj, string propertyName, out string value)
    {
        value = "";
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue jsonValue)
            return false;

        if (!jsonValue.TryGetValue<string>(out var parsed) || parsed == null)
            return false;

        value = parsed;
        return true;
    }
}


