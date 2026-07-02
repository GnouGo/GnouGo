using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal static class StepOutputTypeResolver
{
    public static FlowTypeDescriptor Resolve(
        StepDef step,
        IReadOnlyDictionary<string, WorkflowDef> workflows,
        WorkflowSymbolTable symbols,
        IReadOnlyDictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        IReadOnlyDictionary<string, StepContract> stepContracts)
    {
        return step.Type switch
        {
            "set" => ResolveSet(step, symbols),
            "assert.non_null" => ResolveAssertNonNull(step, symbols),
            "template.render" => ResolveTemplateRender(step),
            "llm.call" => ResolveLlmCall(step),
            "mcp.call" => ResolveMcpCall(step, mcpContracts),
            "workflow.call" => ResolveWorkflowCall(step, workflows),
            "human.input" => ResolveHumanInput(step),
            _ => stepContracts.TryGetValue(step.Type, out var contract)
                ? contract.OutputType
                : FlowTypeDescriptor.Any
        };
    }

    private static FlowTypeDescriptor ResolveSet(StepDef step, WorkflowSymbolTable symbols)
    {
        if (step.OutputSchema != null)
            return FlowTypeDescriptorConverter.FromJsonSchema(step.OutputSchema);

        if (step.Input is not JsonObject input)
            return Object();

        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            var inferred = StepExpressionTypeValidator.InferValueType(value, symbols.WorkflowInputs, symbols.StepOutputs, symbols.DataVariables);
            properties[key] = Property(
                inferred == null || inferred.IsOpaque
                    ? InferFromExample(value)
                    : inferred);
        }

        return FlowTypeDescriptor.Object(properties);
    }

    private static FlowTypeDescriptor ResolveAssertNonNull(StepDef step, WorkflowSymbolTable symbols)
    {
        if (step.Input is not JsonObject input)
            return Object();

        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            var inferred = StepExpressionTypeValidator.InferValueType(value, symbols.WorkflowInputs, symbols.StepOutputs, symbols.DataVariables);
            if (inferred == null || inferred.IsOpaque)
                inferred = InferFromExample(value);
            properties[key] = Property(inferred.RemoveNullDeep());
        }

        return FlowTypeDescriptor.Object(properties);
    }

    private static FlowTypeDescriptor ResolveTemplateRender(StepDef step)
    {
        var mode = TryGetInputString(step, "mode") ?? "text";
        return string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase)
            ? Object(
                ("json", FlowTypeDescriptor.Any),
                ("meta", Object(("engine", FlowTypeDescriptor.String))))
            : Object(
                ("text", FlowTypeDescriptor.String),
                ("meta", Object(("engine", FlowTypeDescriptor.String))));
    }

    private static FlowTypeDescriptor ResolveLlmCall(StepDef step)
    {
        var jsonType = GetStructuredOutputType(step) ?? FlowTypeDescriptor.Any;
        return Object(
            ("text", FlowTypeDescriptor.String),
            ("json", jsonType),
            ("usage", Object()),
            ("meta", Object(("model", FlowTypeDescriptor.String))),
            ("raw", FlowTypeDescriptor.Any));
    }

    private static FlowTypeDescriptor ResolveMcpCall(
        StepDef step,
        IReadOnlyDictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts)
    {
        var input = step.Input as JsonObject;
        var kind = TryGetInputString(step, "kind") ?? "tool";
        var hasLlmAssistedSelection = input?.ContainsKey("prompt") == true;
        var structuredJsonType = GetStructuredOutputType(step);

        if (hasLlmAssistedSelection)
        {
            return Object(
                ("status", FlowTypeDescriptor.String),
                ("selection_mode", FlowTypeDescriptor.String),
                ("text", FlowTypeDescriptor.String),
                ("selection_text", FlowTypeDescriptor.String),
                ("tool_calls", FlowTypeDescriptor.Array()),
                ("results", FlowTypeDescriptor.Array()),
                ("json", structuredJsonType ?? FlowTypeDescriptor.Any));
        }

        if (string.Equals(kind, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            return Object(
                ("status", FlowTypeDescriptor.String),
                ("description", FlowTypeDescriptor.String),
                ("messages", FlowTypeDescriptor.Array()),
                ("text", FlowTypeDescriptor.String));
        }

        var responseType = FlowTypeDescriptor.Any;
        var serverName = TryGetInputString(step, "server");
        var methodName = TryGetInputString(step, "method");
        if (!string.IsNullOrWhiteSpace(serverName)
            && !string.IsNullOrWhiteSpace(methodName)
            && mcpContracts.TryGetValue((serverName, methodName), out var contract))
        {
            responseType = contract.OutputSchema == null
                ? InferFromExample(contract.ExampleResponse)
                : FlowTypeDescriptorConverter.FromJsonSchema(contract.OutputSchema);
        }

        return Object(
            ("status", FlowTypeDescriptor.String),
            ("response", responseType),
            ("error", Object()),
            ("correlation_id", FlowTypeDescriptor.String),
            ("trace_id", FlowTypeDescriptor.String),
            ("results", FlowTypeDescriptor.Array()));
    }

    private static FlowTypeDescriptor ResolveWorkflowCall(
        StepDef step,
        IReadOnlyDictionary<string, WorkflowDef> workflows)
    {
        if (step.Input is JsonObject input
            && input["ref"] is JsonObject refObject
            && string.Equals(TryGetLiteralString(refObject["kind"]) ?? "local", "local", StringComparison.OrdinalIgnoreCase)
            && TryGetLiteralString(refObject["name"]) is { } targetName
            && workflows.TryGetValue(targetName, out var targetWorkflow))
        {
            return Object(
                ("outputs", StepExpressionTypeValidator.OutputsObjectType(targetWorkflow.Outputs)),
                ("workflow", FlowTypeDescriptor.String),
                ("run", Object(
                    ("steps_executed", FlowTypeDescriptor.Number),
                    ("success", FlowTypeDescriptor.Boolean))));
        }

        return Object(
            ("outputs", FlowTypeDescriptor.Any),
            ("workflow", FlowTypeDescriptor.String),
            ("run", Object(
                ("steps_executed", FlowTypeDescriptor.Number),
                ("success", FlowTypeDescriptor.Boolean))));
    }

    private static FlowTypeDescriptor ResolveHumanInput(StepDef step)
    {
        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal)
        {
            ["response"] = Property(FlowTypeDescriptor.Any),
            ["source"] = Property(FlowTypeDescriptor.String)
        };

        if (step.Input is JsonObject input && input["fields"] is JsonArray fields)
        {
            foreach (var fieldNode in fields)
            {
                if (fieldNode is not JsonObject field)
                    continue;
                var name = field["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var type = field["type"]?.GetValue<string>() ?? "string";
                properties[name] = Property(HumanInputFieldType(type));
            }
        }

        return FlowTypeDescriptor.Object(properties);
    }

    private static FlowTypeDescriptor HumanInputFieldType(string type) =>
        type.ToLowerInvariant() switch
        {
            "number" or "integer" => FlowTypeDescriptor.Number,
            "boolean" => FlowTypeDescriptor.Boolean,
            "json" => FlowTypeDescriptor.Any,
            "multiselect" or "checkbox" or "file" or "directory" => FlowTypeDescriptor.Array(),
            _ => FlowTypeDescriptor.String
        };

    private static FlowTypeDescriptor? GetStructuredOutputType(StepDef step)
    {
        if (step.Input is not JsonObject input || input["structured_output"] is not JsonObject structuredOutput)
            return null;

        var contract = JsonSchemaContractValidator.ValidateStructuredOutput(
            structuredOutput,
            allowDynamicSchemaReference: true);
        if (contract.IsDynamic || contract.Errors.Count > 0 || contract.Schema == null)
            return null;

        return FlowTypeDescriptorConverter.FromJsonSchema(contract.Schema);
    }

    private static FlowTypeDescriptor InferFromExample(JsonNode? example)
    {
        return example switch
        {
            JsonObject obj => InferObjectFromExample(obj),
            JsonArray array => FlowTypeDescriptor.Array(array.Count > 0
                ? InferFromExample(array[0])
                : FlowTypeDescriptor.Any),
            JsonValue value when value.TryGetValue<string>(out _) => FlowTypeDescriptor.String,
            JsonValue value when value.TryGetValue<bool>(out _) => FlowTypeDescriptor.Boolean,
            JsonValue value when value.TryGetValue<int>(out _) => FlowTypeDescriptor.Number,
            JsonValue value when value.TryGetValue<long>(out _) => FlowTypeDescriptor.Number,
            JsonValue value when value.TryGetValue<double>(out _) => FlowTypeDescriptor.Number,
            null => FlowTypeDescriptor.Any,
            _ => FlowTypeDescriptor.Any
        };
    }

    private static FlowTypeDescriptor InferObjectFromExample(JsonObject obj)
    {
        var properties = new Dictionary<string, FlowPropertyDescriptor>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
            properties[key] = Property(InferFromExample(value));
        return FlowTypeDescriptor.Object(properties);
    }

    private static FlowTypeDescriptor Object(params (string Name, FlowTypeDescriptor Type)[] properties) =>
        FlowTypeDescriptor.Object(properties.ToDictionary(
            static pair => pair.Name,
            static pair => Property(pair.Type),
            StringComparer.Ordinal));

    private static FlowPropertyDescriptor Property(FlowTypeDescriptor type) => new(type);

    private static string? TryGetInputString(StepDef step, string propertyName)
    {
        if (step.Input is not JsonObject input || !input.TryGetPropertyValue(propertyName, out var node) || node == null)
            return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) && !text.Contains("${", StringComparison.Ordinal))
            return text;

        return null;
    }

    private static string? TryGetLiteralString(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            return null;

        return string.IsNullOrWhiteSpace(text) || text.Contains("${", StringComparison.Ordinal)
            ? null
            : text;
    }
}
