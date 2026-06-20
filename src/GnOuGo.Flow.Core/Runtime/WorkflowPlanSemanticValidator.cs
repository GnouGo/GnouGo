using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal sealed record McpToolOutputContract(
    string ServerName,
    string ToolName,
    JsonNode? InputSchema,
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

    public static int NormalizeMcpCallInputRequests(WorkflowDocument document, IReadOnlyList<McpToolOutputContract>? mcpToolContracts = null)
    {
        var mcpContracts = BuildMcpContractLookup(mcpToolContracts);
        var changes = 0;

        foreach (var workflow in document.Workflows.Values)
            changes += NormalizeMcpCallInputRequests(workflow.Steps, mcpContracts);

        return changes;
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

    private static int NormalizeMcpCallInputRequests(
        IReadOnlyList<StepDef> steps,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts)
    {
        var changes = 0;
        foreach (var step in steps)
        {
            changes += NormalizeMcpCallInputRequest(step, mcpContracts);

            if (step.Steps != null)
                changes += NormalizeMcpCallInputRequests(step.Steps, mcpContracts);

            if (step.Branches != null)
                foreach (var branch in step.Branches)
                    changes += NormalizeMcpCallInputRequests(branch.Steps, mcpContracts);

            if (step.Cases != null)
                foreach (var @case in step.Cases)
                    changes += NormalizeMcpCallInputRequests(@case.Steps, mcpContracts);

            if (step.Default != null)
                changes += NormalizeMcpCallInputRequests(step.Default, mcpContracts);
        }

        return changes;
    }

    private static int NormalizeMcpCallInputRequest(
        StepDef step,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts)
    {
        if (!string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)
            || step.Input is not JsonObject input)
        {
            return 0;
        }

        var kind = TryGetInputString(step, "kind") ?? "tool";
        if (!string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
            return 0;

        var serverName = TryGetInputString(step, "server");
        var methodName = TryGetInputString(step, "method");
        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(methodName))
            return 0;

        if (!mcpContracts.TryGetValue((serverName, methodName), out var contract)
            || contract.InputSchema is not JsonObject inputSchema
            || input["request"] is not JsonObject requestObject)
        {
            return 0;
        }

        return NormalizeJsonNodeAgainstSchema(requestObject, inputSchema);
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
        ValidateMcpCallInputRequest(step, workflowName, mcpContracts, errors);

        var stepIsConditional = !string.IsNullOrWhiteSpace(step.If);

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

            if (!stepIsConditional)
            {
                foreach (var produced in branchProducedContracts)
                    knownContracts[produced.Key] = produced.Value?.DeepClone();
            }
        }
        else if (step.Type == "switch")
        {
            if (step.Cases != null)
            {
                foreach (var @case in step.Cases)
                {
                    ValidateString(@case.When, workflowName, step.Id, "cases.when", knownContracts, allStepIds, errors);

                    // Only one switch branch runs at runtime, so branch-local step outputs are not
                    // guaranteed mappings after the switch. Validate each branch independently.
                    var caseKnown = CloneContracts(knownContracts);
                    ValidateStepList(@case.Steps, workflowName, caseKnown, allStepIds, mcpContracts, errors);
                }
            }

            if (step.Default != null)
            {
                var defaultKnown = CloneContracts(knownContracts);
                ValidateStepList(step.Default, workflowName, defaultKnown, allStepIds, mcpContracts, errors);
            }
        }
        else if (step.Type is "loop.sequential" or "loop.parallel")
        {
            if (step.Steps != null)
            {
                // Loop bodies may execute zero times, so their inner step outputs are not guaranteed
                // mappings after the loop. References inside the loop are still validated in order.
                var loopKnown = CloneContracts(knownContracts);
                ValidateStepList(step.Steps, workflowName, loopKnown, allStepIds, mcpContracts, errors);
            }
        }
        else
        {
            if (step.Steps != null)
            {
                if (stepIsConditional)
                {
                    var conditionalKnown = CloneContracts(knownContracts);
                    ValidateStepList(step.Steps, workflowName, conditionalKnown, allStepIds, mcpContracts, errors);
                }
                else
                {
                    ValidateStepList(step.Steps, workflowName, knownContracts, allStepIds, mcpContracts, errors);
                }
            }
        }

        if (!stepIsConditional && !string.IsNullOrWhiteSpace(step.Id))
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
                        ? BuildOpaqueOutputSuggestion(invalidPath, referencedStepId)
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

    private static string BuildOpaqueOutputSuggestion(string invalidPath, string referencedStepId)
    {
        var responseIndex = invalidPath.IndexOf(".response", StringComparison.Ordinal);
        if (responseIndex >= 0)
        {
            var responsePath = invalidPath[..responseIndex] + ".response";
            return $"Do not invent fields under '{responsePath}'. Use json({responsePath}), or add an llm.call normalization step with structured_output before accessing named fields.";
        }

        var stepPath = $"data.steps.{referencedStepId}";
        return $"Do not invent fields under opaque output '{stepPath}'. Pass the whole value onward, or add an llm.call normalization step with structured_output before accessing named fields.";
    }

    private static void ValidateMcpCallInputRequest(
        StepDef step,
        string workflowName,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        if (!string.Equals(step.Type, "mcp.call", StringComparison.Ordinal)
            || step.Input is not JsonObject input)
        {
            return;
        }

        var kind = TryGetInputString(step, "kind") ?? "tool";
        if (!string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
            return;

        var serverName = TryGetInputString(step, "server");
        if (string.IsNullOrWhiteSpace(serverName))
            return;

        var methodTargets = GetLiteralMcpMethodTargets(step);
        foreach (var (methodName, methodField) in methodTargets)
        {
            if (!ValidateMcpCallTargetExists(step, workflowName, serverName, methodName, methodField, mcpContracts, errors))
                continue;

            if (!mcpContracts.TryGetValue((serverName, methodName), out var contract)
                || contract.InputSchema is not JsonObject inputSchema)
            {
                continue;
            }

            var requestNode = input["request"];
            var requestObject = requestNode as JsonObject ?? new JsonObject();
            var schemaErrors = new List<SchemaValidationError>();
            ValidateJsonNodeAgainstSchema(requestObject, inputSchema, "", schemaErrors);
            ValidateMcpRequestCompatibilityConventions(requestObject, inputSchema, "", schemaErrors);
            if (schemaErrors.Count == 0)
                continue;

            foreach (var schemaError in schemaErrors)
            {
                var invalidPath = string.IsNullOrEmpty(schemaError.Path)
                    ? "input.request"
                    : $"input.request.{schemaError.Path}";

                errors.Add(new WorkflowSemanticValidationError
                {
                    Code = "MCP_REQUEST_SCHEMA_INVALID",
                    WorkflowName = workflowName,
                    StepId = step.Id,
                    Field = "input.request",
                    InvalidPath = invalidPath,
                    AllowedPaths = EnumerateAllowedPaths("input.request", inputSchema).Take(64).ToArray(),
                    Suggestion = $"Align `mcp.call` request with MCP tool schema for server '{serverName}' method '{methodName}'.",
                    Message = $"mcp.call request for '{serverName}/{methodName}' is invalid: {schemaError.Message}"
                });
            }
        }
    }

    private static IReadOnlyList<(string MethodName, string Field)> GetLiteralMcpMethodTargets(StepDef step)
    {
        if (step.Input is not JsonObject input)
            return Array.Empty<(string, string)>();

        // Runtime batch selection takes precedence whenever `methods` is present.
        if (input.ContainsKey("methods"))
        {
            if (input["methods"] is not JsonArray methods)
                return Array.Empty<(string, string)>();

            var targets = new List<(string, string)>();
            for (var i = 0; i < methods.Count; i++)
            {
                if (methods[i] is JsonValue value
                    && value.TryGetValue<string>(out var methodName)
                    && !string.IsNullOrWhiteSpace(methodName)
                    && !methodName.Contains("${", StringComparison.Ordinal))
                {
                    targets.Add((methodName, $"input.methods[{i}]"));
                }
            }

            return targets;
        }

        var singleMethod = TryGetInputString(step, "method");
        return string.IsNullOrWhiteSpace(singleMethod)
            ? Array.Empty<(string, string)>()
            : new[] { (singleMethod, "input.method") };
    }

    private static bool ValidateMcpCallTargetExists(
        StepDef step,
        string workflowName,
        string serverName,
        string methodName,
        string methodField,
        Dictionary<(string ServerName, string ToolName), McpToolOutputContract> mcpContracts,
        List<WorkflowSemanticValidationError> errors)
    {
        if (mcpContracts.Count == 0)
            return true;

        var knownServers = mcpContracts.Keys
            .Select(key => key.ServerName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (!knownServers.Contains(serverName, StringComparer.Ordinal))
        {
            errors.Add(new WorkflowSemanticValidationError
            {
                Code = "MCP_SERVER_UNKNOWN",
                WorkflowName = workflowName,
                StepId = step.Id,
                Field = "input.server",
                InvalidPath = $"input.server:{serverName}",
                AllowedPaths = knownServers.Select(name => $"mcp.server:{name}").ToArray(),
                Suggestion = "Use one of the MCP servers discovered for this plan, or add an `mcp.list` discovery step before calling it.",
                Message = $"mcp.call references unknown MCP server '{serverName}'."
            });
            return false;
        }

        if (mcpContracts.ContainsKey((serverName, methodName)))
            return true;

        var knownMethods = mcpContracts.Keys
            .Where(key => string.Equals(key.ServerName, serverName, StringComparison.Ordinal))
            .Select(key => key.ToolName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        errors.Add(new WorkflowSemanticValidationError
        {
            Code = "MCP_METHOD_UNKNOWN",
            WorkflowName = workflowName,
            StepId = step.Id,
            Field = methodField,
            InvalidPath = $"{methodField}:{methodName}",
            AllowedPaths = knownMethods.Select(name => $"mcp.server:{serverName}.method:{name}").ToArray(),
            Suggestion = $"Use one of the tools discovered for MCP server '{serverName}', or inspect the server with `mcp.list` before calling it.",
            Message = $"mcp.call references unknown MCP method '{methodName}' on server '{serverName}'."
        });
        return false;
    }

    private sealed record SchemaValidationError(string Path, string Message);

    private static void ValidateMcpRequestCompatibilityConventions(
        JsonObject request,
        JsonObject inputSchema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (inputSchema["properties"] is not JsonObject properties)
            return;

        foreach (var (propertyName, propertySchema) in properties)
        {
            if (!IsExplicitPaginationNumberProperty(propertyName, propertySchema))
                continue;

            var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
            if (!request.TryGetPropertyValue(propertyName, out var value) || value is null)
            {
                errors.Add(new SchemaValidationError(
                    propertyPath,
                    "missing explicit numeric pagination property; send an unquoted number such as 30 instead of omitting it"));
                continue;
            }

            if (value is JsonObject childObject && propertySchema is JsonObject childSchema)
                ValidateMcpRequestCompatibilityConventions(childObject, childSchema, propertyPath, errors);
        }
    }

    private static bool IsExplicitPaginationNumberProperty(string propertyName, JsonNode? propertySchema)
    {
        if (!string.Equals(propertyName, "perPage", StringComparison.Ordinal))
            return false;

        if (propertySchema is not JsonObject schemaObject)
            return false;

        var typeName = ReadSchemaType(schemaObject);
        return string.Equals(typeName, "number", StringComparison.Ordinal)
            || string.Equals(typeName, "integer", StringComparison.Ordinal);
    }

    private static void ValidateJsonNodeAgainstSchema(
        JsonNode? value,
        JsonNode? schema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (schema is not JsonObject schemaObject)
            return;

        if (schemaObject["anyOf"] is JsonArray anyOf)
        {
            if (MatchesAnySchemaVariant(value, anyOf))
                return;

            errors.Add(new SchemaValidationError(path, "value does not match any allowed schema variant"));
            return;
        }

        if (schemaObject["oneOf"] is JsonArray oneOf)
        {
            if (MatchesAnySchemaVariant(value, oneOf))
                return;

            errors.Add(new SchemaValidationError(path, "value does not match any allowed schema variant"));
            return;
        }

        var typeName = ReadSchemaType(schemaObject);
        switch (typeName)
        {
            case "object":
                ValidateObjectAgainstSchema(value, schemaObject, path, errors);
                break;
            case "array":
                ValidateArrayAgainstSchema(value, schemaObject, path, errors);
                break;
            case "string":
                ValidatePrimitiveType(value, "string", path, errors);
                break;
            case "number":
                ValidatePrimitiveType(value, "number", path, errors);
                break;
            case "integer":
                ValidatePrimitiveType(value, "integer", path, errors);
                break;
            case "boolean":
                ValidatePrimitiveType(value, "boolean", path, errors);
                break;
            case "null":
                ValidatePrimitiveType(value, "null", path, errors);
                break;
            default:
                break;
        }
    }

    private static bool MatchesAnySchemaVariant(JsonNode? value, JsonArray variants)
    {
        foreach (var variant in variants)
        {
            var variantErrors = new List<SchemaValidationError>();
            ValidateJsonNodeAgainstSchema(value, variant, string.Empty, variantErrors);
            if (variantErrors.Count == 0)
                return true;
        }

        return false;
    }

    private static void ValidateObjectAgainstSchema(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (IsDynamicExpressionString(value))
            return;

        if (value is not JsonObject obj)
        {
            errors.Add(new SchemaValidationError(path, "expected object"));
            return;
        }

        var properties = schema["properties"] as JsonObject;
        var required = schema["required"] as JsonArray;
        if (required != null)
        {
            foreach (var requiredNode in required)
            {
                var requiredName = requiredNode?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(requiredName))
                    continue;

                if (!obj.ContainsKey(requiredName))
                {
                    var requiredPath = string.IsNullOrEmpty(path) ? requiredName : $"{path}.{requiredName}";
                    errors.Add(new SchemaValidationError(requiredPath, "missing required property"));
                }
            }
        }

        foreach (var (propertyName, propertyValue) in obj)
        {
            if (properties != null && properties.TryGetPropertyValue(propertyName, out var propertySchema) && propertySchema != null)
            {
                var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
                ValidateJsonNodeAgainstSchema(propertyValue, propertySchema, propertyPath, errors);
                continue;
            }

            if (!AllowsAdditionalProperties(schema))
            {
                var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
                errors.Add(new SchemaValidationError(propertyPath, "property is not allowed by schema"));
                continue;
            }

            if (schema["additionalProperties"] is JsonObject additionalSchema)
            {
                var propertyPath = string.IsNullOrEmpty(path) ? propertyName : $"{path}.{propertyName}";
                ValidateJsonNodeAgainstSchema(propertyValue, additionalSchema, propertyPath, errors);
            }
        }
    }

    private static void ValidateArrayAgainstSchema(
        JsonNode? value,
        JsonObject schema,
        string path,
        List<SchemaValidationError> errors)
    {
        if (IsDynamicExpressionString(value))
            return;

        if (value is not JsonArray array)
        {
            errors.Add(new SchemaValidationError(path, "expected array"));
            return;
        }

        if (schema["items"] == null)
            return;

        for (var i = 0; i < array.Count; i++)
        {
            var itemPath = string.IsNullOrEmpty(path) ? $"[{i}]" : $"{path}[{i}]";
            ValidateJsonNodeAgainstSchema(array[i], schema["items"], itemPath, errors);
        }
    }

    private static void ValidatePrimitiveType(
        JsonNode? value,
        string expectedType,
        string path,
        List<SchemaValidationError> errors)
    {
        if (IsDynamicExpressionString(value))
            return;

        if (value is null)
        {
            if (!string.Equals(expectedType, "null", StringComparison.Ordinal))
                errors.Add(new SchemaValidationError(path, $"expected {expectedType} but got null"));
            return;
        }

        if (value is not JsonValue jsonValue)
        {
            errors.Add(new SchemaValidationError(path, $"expected {expectedType}"));
            return;
        }

        var valid = expectedType switch
        {
            "string" => jsonValue.TryGetValue<string>(out _),
            "number" => IsJsonNumber(jsonValue),
            "integer" => jsonValue.TryGetValue<long>(out _) || jsonValue.TryGetValue<int>(out _),
            "boolean" => jsonValue.TryGetValue<bool>(out _),
            "null" => false,
            _ => true
        };

        if (!valid)
            errors.Add(new SchemaValidationError(path, $"expected {expectedType}"));
    }

    private static bool IsJsonNumber(JsonValue jsonValue) =>
        jsonValue.TryGetValue<double>(out _)
        || jsonValue.TryGetValue<float>(out _)
        || jsonValue.TryGetValue<decimal>(out _)
        || jsonValue.TryGetValue<long>(out _)
        || jsonValue.TryGetValue<int>(out _)
        || jsonValue.TryGetValue<short>(out _)
        || jsonValue.TryGetValue<byte>(out _);

    private static int NormalizeJsonNodeAgainstSchema(JsonNode? value, JsonNode? schema)
    {
        if (schema is not JsonObject schemaObject || value == null)
            return 0;

        if (schemaObject["anyOf"] is JsonArray anyOf)
            return NormalizeAgainstSingleMatchingVariant(value, anyOf);

        if (schemaObject["oneOf"] is JsonArray oneOf)
            return NormalizeAgainstSingleMatchingVariant(value, oneOf);

        var typeName = ReadSchemaType(schemaObject);
        return typeName switch
        {
            "object" => NormalizeObjectAgainstSchema(value, schemaObject),
            "array" => NormalizeArrayAgainstSchema(value, schemaObject),
            _ => 0
        };
    }

    private static int NormalizeAgainstSingleMatchingVariant(JsonNode value, JsonArray variants)
    {
        JsonObject? selectedVariant = null;
        foreach (var variant in variants)
        {
            if (variant is not JsonObject variantObject)
                continue;

            var errors = new List<SchemaValidationError>();
            ValidateJsonNodeAgainstSchema(value, variantObject, string.Empty, errors);
            if (errors.Count == 0)
                return 0;

            var clone = value.DeepClone();
            var changes = NormalizeJsonNodeAgainstSchema(clone, variantObject);
            if (changes == 0)
                continue;

            errors.Clear();
            ValidateJsonNodeAgainstSchema(clone, variantObject, string.Empty, errors);
            if (errors.Count == 0)
            {
                if (selectedVariant != null)
                    return 0;

                selectedVariant = variantObject;
            }
        }

        return selectedVariant == null ? 0 : NormalizeJsonNodeAgainstSchema(value, selectedVariant);
    }

    private static int NormalizeObjectAgainstSchema(JsonNode value, JsonObject schema)
    {
        if (IsDynamicExpressionString(value) || value is not JsonObject obj)
            return 0;

        var changes = 0;
        var properties = schema["properties"] as JsonObject;
        foreach (var propertyName in obj.Select(kv => kv.Key).ToArray())
        {
            var propertyValue = obj[propertyName];
            JsonNode? propertySchema = null;

            if (properties != null)
                properties.TryGetPropertyValue(propertyName, out propertySchema);

            propertySchema ??= schema["additionalProperties"] as JsonObject;
            if (propertySchema == null)
                continue;

            changes += NormalizeJsonNodeAgainstSchema(propertyValue, propertySchema);
            if (TryCoerceJsonValue(propertyValue, propertySchema, out var coerced))
            {
                obj[propertyName] = coerced;
                changes++;
            }
        }

        return changes;
    }

    private static int NormalizeArrayAgainstSchema(JsonNode value, JsonObject schema)
    {
        if (IsDynamicExpressionString(value) || value is not JsonArray array || schema["items"] == null)
            return 0;

        var itemSchema = schema["items"];
        var changes = 0;
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i];
            changes += NormalizeJsonNodeAgainstSchema(item, itemSchema);
            if (TryCoerceJsonValue(item, itemSchema, out var coerced))
            {
                array[i] = coerced;
                changes++;
            }
        }

        return changes;
    }

    private static bool TryCoerceJsonValue(JsonNode? value, JsonNode? schema, out JsonNode? coerced)
    {
        coerced = null;
        if (schema is not JsonObject schemaObject || value is not JsonValue jsonValue || IsDynamicExpressionString(value))
            return false;

        var typeName = ReadSchemaType(schemaObject);
        if (typeName == "number" && jsonValue.TryGetValue<string>(out var numberText)
            && double.TryParse(numberText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            coerced = JsonValue.Create(number);
            return true;
        }

        if (typeName == "integer" && jsonValue.TryGetValue<string>(out var integerText)
            && long.TryParse(integerText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer))
        {
            coerced = JsonValue.Create(integer);
            return true;
        }

        if (typeName == "boolean" && jsonValue.TryGetValue<string>(out var booleanText)
            && bool.TryParse(booleanText, out var boolean))
        {
            coerced = JsonValue.Create(boolean);
            return true;
        }

        return false;
    }

    private static bool IsDynamicExpressionString(JsonNode? value)
    {
        return value is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
            && text != null
            && text.Contains("${", StringComparison.Ordinal);
    }

    private static string? ReadSchemaType(JsonObject schema)
    {
        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var singleType))
            return singleType;

        if (schema["type"] is JsonArray typeArray)
        {
            var first = typeArray
                .Select(node => node is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null)
                .FirstOrDefault(parsed => !string.IsNullOrWhiteSpace(parsed) && !string.Equals(parsed, "null", StringComparison.Ordinal));
            return first;
        }

        return null;
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
            "workflow.call" => ObjectSchema(("outputs", OpaqueSchema()), ("workflow", StringSchema())),
            "workflow.plan" => ObjectSchema(("workflow", ObjectSchema()), ("yaml", StringSchema()), ("meta", ObjectSchema()), ("diagnostics", ArraySchema())),
            "workflow.execute" => ObjectSchema(("outputs", OpaqueSchema()), ("workflow", StringSchema()), ("run", ObjectSchema(("steps_executed", NumberSchema()), ("success", BooleanSchema())))),
            "sequence" => ObjectSchema(("steps", ObjectSchema()), ("count", NumberSchema())),
            "parallel" => ObjectSchema(("branches", ArraySchema()), ("count", NumberSchema())),
            "loop.sequential" or "loop.parallel" => ObjectSchema(("results", ArraySchema()), ("count", NumberSchema())),
            "switch" => ObjectSchema(("matched", StringSchema()), ("steps", ObjectSchema())),
            "emit" => ObjectSchema(("event", OpaqueSchema()), ("status", StringSchema())),
            "human.input" => BuildHumanInputOutputSchema(step),
            _ => OpaqueSchema()
        };
    }

    private static JsonNode BuildHumanInputOutputSchema(StepDef step)
    {
        var properties = new List<(string Name, JsonNode? Schema)>
        {
            ("response", OpaqueSchema()),
            ("source", StringSchema())
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
                properties.Add((name, HumanInputFieldSchema(type)));
            }
        }

        return ObjectSchema(properties.ToArray());
    }

    private static JsonNode HumanInputFieldSchema(string type) =>
        type.ToLowerInvariant() switch
        {
            "number" or "integer" => NumberSchema(),
            "boolean" => BooleanSchema(),
            "json" => OpaqueSchema(),
            "multiselect" or "checkbox" or "file" or "directory" => ArraySchema(),
            _ => StringSchema()
        };

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
