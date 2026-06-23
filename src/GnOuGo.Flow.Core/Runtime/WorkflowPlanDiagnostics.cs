using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;

namespace GnOuGo.Flow.Core.Runtime;

internal static class WorkflowPlanDiagnostics
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static JsonObject BuildValidationFailureDetails(
        IReadOnlyList<ValidationError> validationErrors,
        WorkflowSemanticValidationException? semanticException,
        Exception? compilationException,
        string phase = "validation")
    {
        var diagnostics = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var error in validationErrors)
            AddDiagnostic(diagnostics, seen, BuildValidationDiagnostic(error, "workflow_validation"));

        if (semanticException != null)
        {
            foreach (var error in semanticException.Errors)
                AddDiagnostic(diagnostics, seen, BuildSemanticDiagnostic(error));
        }

        if (compilationException is WorkflowCompilationException compilation)
        {
            foreach (var error in compilation.Errors)
                AddDiagnostic(diagnostics, seen, BuildValidationDiagnostic(error, "compilation"));
        }
        else if (compilationException != null)
        {
            AddDiagnostic(
                diagnostics,
                seen,
                BuildExceptionDiagnostic(
                    code: InferPlanErrorCode(compilationException.Message),
                    phase: "compilation",
                    message: compilationException.Message,
                    exception: compilationException));
        }

        return BuildPlanDetails(
            phase,
            diagnostics,
            "Generated workflow validation failed.",
            new[]
            {
                "Fix each diagnostic in order, starting with YAML shape and input contract errors before semantic references.",
                "Use the diagnostic location, expected value, and allowed paths to make the smallest valid YAML change.",
                "Do not invent step types, MCP servers, MCP methods, request fields, or step output fields that are not listed in the diagnostics."
            });
    }

    public static JsonObject BuildDryRunFailureDetails(
        string code,
        string message,
        string failureKind,
        Exception? exception = null,
        JsonNode? runtimeDetails = null)
    {
        var diagnostics = new JsonArray();
        diagnostics.Add((JsonNode)BuildDryRunDiagnostic(code, message, failureKind, exception, runtimeDetails));

        return BuildPlanDetails(
            "dry_run",
            diagnostics,
            "Generated workflow dry_run failed.",
            new[]
            {
                "Dry-run executes the generated workflow with deterministic fake providers, so failures usually mean an expression, input contract, MCP request, or step dependency is invalid.",
                "Fix the exact failing step or field, then re-check downstream references that consume it.",
                "Keep sample-safe values and do not replace missing required data with secrets, environment variables, empty strings, or fake production data."
            });
    }

    public static JsonObject BuildExceptionDetails(
        string code,
        string phase,
        string message,
        Exception? exception = null)
    {
        var diagnostics = new JsonArray();
        diagnostics.Add((JsonNode)BuildExceptionDiagnostic(code, phase, message, exception));

        return BuildPlanDetails(
            phase,
            diagnostics,
            message,
            new[]
            {
                "Use the diagnostic code and message to repair the generated YAML before retrying.",
                "If this is a parser error, fix YAML syntax and root structure before changing workflow logic."
            });
    }

    public static string FormatValidationErrors(IReadOnlyList<ValidationError> errors)
    {
        return string.Join("; ", errors.Select(error =>
        {
            var location = new List<string>();
            if (!string.IsNullOrWhiteSpace(error.WorkflowName))
                location.Add($"workflow '{error.WorkflowName}'");
            if (!string.IsNullOrWhiteSpace(error.StepId))
                location.Add($"step '{error.StepId}'");
            if (!string.IsNullOrWhiteSpace(error.Field))
                location.Add($"field '{error.Field}'");

            var prefix = location.Count > 0 ? $"[{string.Join(", ", location)}] " : "";
            return $"{prefix}{error.Code}: {error.Message}";
        }));
    }

    public static string ToPromptJson(JsonNode node) => node.ToJsonString(DiagnosticJsonOptions);

    public static string BuildStructuredPlanError(Exception ex, int attempt)
    {
        var message = ex.Message.Trim();
        var code = ex is WorkflowRuntimeException workflowEx
            ? InferPlanErrorCode(message, workflowEx.Code)
            : InferPlanErrorCode(message);

        var root = new JsonObject
        {
            ["attempt"] = attempt,
            ["code"] = code,
            ["message"] = message,
            ["legacy_summary"] = $"attempt={attempt}; code={code}; message={message}"
        };

        if (ex is WorkflowRuntimeException { Details: not null } runtimeEx)
        {
            var details = ClonePromptSafeDetails(runtimeEx.Details);
            if (details != null)
                root["details"] = details;
        }

        return ToPromptJson(root);
    }

    public static string InferPlanErrorCode(string message, string? exceptionCode = null)
    {
        var lower = message.ToLowerInvariant();

        if (lower.Contains("mcp_server_unknown", StringComparison.Ordinal))
            return "MCP_SERVER_UNKNOWN";
        if (lower.Contains("mcp_method_unknown", StringComparison.Ordinal))
            return "MCP_METHOD_UNKNOWN";
        if (lower.Contains("mcp tool", StringComparison.Ordinal) && lower.Contains("does not exist", StringComparison.Ordinal))
            return "MCP_METHOD_UNKNOWN";
        if (lower.Contains("available tools", StringComparison.Ordinal) && lower.Contains("not found", StringComparison.Ordinal))
            return "MCP_METHOD_UNKNOWN";
        if (lower.Contains("mcp_request_schema_invalid", StringComparison.Ordinal)
            || lower.Contains("mcp.call request", StringComparison.Ordinal) && lower.Contains("invalid", StringComparison.Ordinal))
            return "MCP_REQUEST_SCHEMA_INVALID";
        if (lower.Contains("expr_type_mismatch", StringComparison.Ordinal)
            || lower.Contains("resolves to", StringComparison.Ordinal) && lower.Contains("contract requires", StringComparison.Ordinal))
            return ErrorCodes.ExprTypeMismatch;
        if (lower.Contains("mcp_server_not_found", StringComparison.Ordinal)
            || lower.Contains("mcp server", StringComparison.Ordinal) && lower.Contains("not found", StringComparison.Ordinal))
            return ErrorCodes.McpServerNotFound;
        if (lower.Contains("missing required field 'workflows'", StringComparison.Ordinal))
            return "MISSING_ROOT_KEY_WORKFLOWS";
        if (lower.Contains("missing required field 'version'", StringComparison.Ordinal))
            return "MISSING_ROOT_KEY_VERSION";
        if (lower.Contains("missing required field 'name'", StringComparison.Ordinal))
            return "MISSING_ROOT_KEY_NAME";
        if (lower.Contains("skill_required", StringComparison.Ordinal) || lower.Contains("top-level 'skill'", StringComparison.Ordinal))
            return "MISSING_ROOT_KEY_SKILL";
        if (lower.Contains("step_type_unknown", StringComparison.Ordinal))
            return "UNKNOWN_STEP_TYPE";
        if (lower.Contains("missing_steps", StringComparison.Ordinal)
            || lower.Contains("missing_branches", StringComparison.Ordinal)
            || lower.Contains("missing_cases", StringComparison.Ordinal))
            return "INVALID_CONTAINER_SHAPE";
        if (lower.Contains("step_reference_not_available", StringComparison.Ordinal)
            || lower.Contains("step_reference_unknown", StringComparison.Ordinal)
            || lower.Contains("semantic_mapping_error", StringComparison.Ordinal))
            return "SEMANTIC_MAPPING_ERROR";
        if (lower.Contains("opaque_response_deep_access", StringComparison.Ordinal))
            return "OPAQUE_RESPONSE_DEEP_ACCESS";
        if (lower.Contains("step_output_property_unknown", StringComparison.Ordinal))
            return "STEP_OUTPUT_PROPERTY_UNKNOWN";
        if (lower.Contains("yaml", StringComparison.Ordinal))
            return "YAML_PARSE_ERROR";
        if (lower.Contains("not allowed by policy", StringComparison.Ordinal) || lower.Contains("denied by policy", StringComparison.Ordinal))
            return "POLICY_ERROR";
        if (lower.Contains("exceeds limit", StringComparison.Ordinal))
            return "LIMIT_ERROR";

        return string.IsNullOrWhiteSpace(exceptionCode) ? "VALIDATION_ERROR" : exceptionCode;
    }

    private static JsonObject BuildPlanDetails(
        string phase,
        JsonArray diagnostics,
        string summary,
        IEnumerable<string> llmGuidance)
    {
        return new JsonObject
        {
            ["ok"] = false,
            ["phase"] = phase,
            ["summary"] = BuildDiagnosticSummary(diagnostics, summary),
            ["diagnostics"] = diagnostics,
            ["llm_guidance"] = new JsonArray(llmGuidance.Select(static item => (JsonNode)JsonValue.Create(item)!).ToArray())
        };
    }

    private static JsonNode? ClonePromptSafeDetails(JsonNode details)
    {
        if (details is not JsonObject obj)
            return details.DeepClone();

        var clone = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (key is "generated_yaml" or "invalid_yaml" or "yaml")
                continue;

            clone[key] = value?.DeepClone();
        }

        return clone.Count == 0 ? null : clone;
    }

    private static string BuildDiagnosticSummary(JsonArray diagnostics, string fallback)
    {
        if (diagnostics.Count == 0)
            return fallback;

        var codes = diagnostics
            .OfType<JsonObject>()
            .Select(static diagnostic => GetString(diagnostic, "code"))
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        return $"{diagnostics.Count} diagnostic(s): {string.Join(", ", codes)}";
    }

    private static void AddDiagnostic(JsonArray diagnostics, HashSet<string> seen, JsonObject diagnostic)
    {
        var key = string.Join("|", new[]
        {
            GetString(diagnostic, "code"),
            GetString(diagnostic, "workflow"),
            GetString(diagnostic, "step"),
            GetString(diagnostic, "field"),
            GetString(diagnostic, "message")
        });

        if (seen.Add(key))
            diagnostics.Add((JsonNode)diagnostic);
    }

    private static JsonObject BuildValidationDiagnostic(ValidationError error, string phase)
    {
        var hint = BuildValidationHint(error);
        var obj = new JsonObject
        {
            ["code"] = error.Code,
            ["phase"] = phase,
            ["message"] = error.Message,
            ["location"] = BuildLocation(error.WorkflowName, error.StepId, error.Field),
            ["hint"] = hint,
            ["llm_guidance"] = BuildValidationLlmGuidance(error, hint)
        };

        AddOptional(obj, "workflow", error.WorkflowName);
        AddOptional(obj, "step", error.StepId);
        AddOptional(obj, "field", error.Field);
        AddOptional(obj, "expected", BuildValidationExpected(error));

        return obj;
    }

    private static JsonObject BuildSemanticDiagnostic(WorkflowSemanticValidationError error)
    {
        var hint = string.IsNullOrWhiteSpace(error.Suggestion)
            ? BuildSemanticHint(error)
            : error.Suggestion;

        var obj = new JsonObject
        {
            ["code"] = error.Code,
            ["phase"] = "semantic_validation",
            ["message"] = error.Message,
            ["location"] = BuildLocation(error.WorkflowName, error.StepId, error.Field),
            ["field"] = error.Field,
            ["invalid_path"] = error.InvalidPath,
            ["allowed_paths"] = new JsonArray(error.AllowedPaths.Select(static path => (JsonNode)JsonValue.Create(path)!).ToArray()),
            ["hint"] = hint,
            ["llm_guidance"] = BuildSemanticLlmGuidance(error, hint)
        };

        AddOptional(obj, "workflow", error.WorkflowName);
        AddOptional(obj, "step", error.StepId);
        AddOptional(obj, "expected", BuildSemanticExpected(error));

        return obj;
    }

    private static JsonObject BuildDryRunDiagnostic(
        string code,
        string message,
        string failureKind,
        Exception? exception,
        JsonNode? runtimeDetails)
    {
        var diagnosticCode = InferPlanErrorCode(message, code);
        var hint = BuildDryRunHint(diagnosticCode, message);
        var obj = new JsonObject
        {
            ["code"] = diagnosticCode,
            ["runtime_error_code"] = code,
            ["phase"] = "dry_run",
            ["failure_kind"] = failureKind,
            ["message"] = message,
            ["location"] = ExtractLocationFromRuntimeDetails(runtimeDetails) ?? "dry_run",
            ["hint"] = hint,
            ["llm_guidance"] = BuildDryRunLlmGuidance(diagnosticCode, hint)
        };

        if (runtimeDetails != null)
            obj["runtime_details"] = runtimeDetails.DeepClone();
        if (exception != null)
            AddExceptionInfo(obj, exception);

        return obj;
    }

    private static JsonObject BuildExceptionDiagnostic(string code, string phase, string message, Exception? exception)
    {
        var diagnosticCode = InferPlanErrorCode(message, code);
        var hint = exception is WorkflowParseException
            ? "Fix YAML syntax at the reported line and column before changing workflow logic."
            : "Inspect the message and repair the generated workflow shape or contract that triggered this exception.";

        var obj = new JsonObject
        {
            ["code"] = diagnosticCode,
            ["phase"] = phase,
            ["message"] = message,
            ["location"] = exception is WorkflowParseException parseEx
                ? $"yaml:{parseEx.Line}:{parseEx.Column}"
                : phase,
            ["hint"] = hint,
            ["llm_guidance"] = hint
        };

        if (exception != null)
            AddExceptionInfo(obj, exception);

        return obj;
    }

    private static string BuildLocation(string? workflowName, string? stepId, string? field)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workflowName))
            parts.Add($"workflow:{workflowName}");
        if (!string.IsNullOrWhiteSpace(stepId))
            parts.Add($"step:{stepId}");
        if (!string.IsNullOrWhiteSpace(field))
            parts.Add($"field:{field}");

        return parts.Count == 0 ? "$" : string.Join("/", parts);
    }

    private static string BuildValidationHint(ValidationError error)
    {
        if (error.Message.StartsWith("Unknown YAML field", StringComparison.OrdinalIgnoreCase))
            return "Remove the unknown YAML key, or move it to the documented location. Common fields stay at step level; executor-specific arguments go under step.input.";

        return error.Code switch
        {
            ErrorCodes.SkillRequired => "Add a top-level `skill` block with description, tags, inputs, and outputs so routers and LLM repair can understand the workflow contract.",
            ErrorCodes.StepTypeUnknown => "Replace the step type with one exact registered step type from the available DSL snippets.",
            ErrorCodes.ExprParse => "Fix the expression syntax inside `${...}`; validate function names, parentheses, and quoted string literals.",
            ErrorCodes.InputValidation => "Align this field with the step input contract and preserve JSON/YAML scalar types exactly.",
            ErrorCodes.LlmSchema => "Use a valid structured_output schema. Prefer `schema_inline` with standard JSON Schema object fields.",
            ErrorCodes.WorkflowCycleDetected => "Break local workflow.call cycles so the call graph is acyclic.",
            "DUPLICATE_STEP_ID" => "Rename one step id. Step ids must be unique within the workflow, including nested branches and cases.",
            "DSL_VERSION" => "Use workflow DSL version 1.",
            "NO_WORKFLOWS" => "Add a non-empty top-level `workflows` object.",
            "EMPTY_STEPS" => "Add at least one executable step to this workflow, or remove the empty workflow.",
            "MISSING_STEPS" => "For sequence and loop steps, put child steps in a step-level `steps:` array.",
            "MISSING_BRANCHES" => "For parallel steps, add step-level `branches:` entries, each with its own `steps:` array.",
            "MISSING_CASES" => "For switch steps, add step-level `cases:` entries with `when:` and `steps:`.",
            "INVALID_ENTRYPOINT" => "Set `entrypoint` to an existing workflow name, or remove it so `main` can be used.",
            "INVALID_EXPORT" => "Export only workflow names that exist in the document.",
            "INVALID_WORKFLOW_REF" => "Point workflow.call ref.name to a local workflow that exists, or add the missing workflow.",
            _ => "Fix the indicated workflow field and rerun validation."
        };
    }

    private static string? BuildValidationExpected(ValidationError error)
    {
        return error.Code switch
        {
            ErrorCodes.SkillRequired => "top-level skill object",
            ErrorCodes.StepTypeUnknown => "registered step type",
            ErrorCodes.InputValidation => "value matching the step input contract",
            ErrorCodes.LlmSchema => "valid JSON Schema structured_output",
            "DUPLICATE_STEP_ID" => "unique step id",
            "DSL_VERSION" => "version: 1",
            "NO_WORKFLOWS" => "non-empty workflows mapping",
            "EMPTY_STEPS" or "MISSING_STEPS" => "non-empty steps array",
            "MISSING_BRANCHES" => "non-empty branches array",
            "MISSING_CASES" => "non-empty cases array",
            "INVALID_ENTRYPOINT" or "INVALID_EXPORT" or "INVALID_WORKFLOW_REF" => "existing workflow name",
            _ => null
        };
    }

    private static string BuildValidationLlmGuidance(ValidationError error, string hint)
    {
        if (!string.IsNullOrWhiteSpace(error.StepId))
            return $"Repair step '{error.StepId}' first. {hint}";

        if (!string.IsNullOrWhiteSpace(error.WorkflowName))
            return $"Repair workflow '{error.WorkflowName}' first. {hint}";

        return hint;
    }

    private static string BuildSemanticHint(WorkflowSemanticValidationError error)
    {
        return error.Code switch
        {
            "STEP_REFERENCE_NOT_AVAILABLE" => "Move the producing step earlier, move the consuming reference later, or create a guaranteed normalization step before reading it.",
            "STEP_REFERENCE_UNKNOWN" => "Reference an existing previous step id, or add the missing producing step before this expression.",
            "OPAQUE_RESPONSE_DEEP_ACCESS" => "Do not invent fields under an opaque response. Pass the whole response or normalize it with llm.call structured_output.",
            "STEP_OUTPUT_PROPERTY_UNKNOWN" => "Use one of the allowed output paths or add a normalizer step that produces the desired property.",
            "MCP_REQUEST_SCHEMA_INVALID" => "Align input.request with the discovered MCP tool input schema.",
            "MCP_CALL_INPUT_FIELD_UNKNOWN" => "Move MCP tool arguments under input.request; keep only mcp.call envelope fields at input top level.",
            "MCP_METHOD_UNKNOWN" => "Use one exact MCP tool name from the discovered server catalog.",
            "MCP_SERVER_UNKNOWN" => "Use one exact MCP server name from discovery.",
            ErrorCodes.ExprTypeMismatch => "Use an expression whose resolved type matches the destination contract.",
            _ => "Repair the generated workflow so the semantic contract can be proven statically."
        };
    }

    private static string? BuildSemanticExpected(WorkflowSemanticValidationError error)
    {
        return error.Code switch
        {
            "STEP_REFERENCE_NOT_AVAILABLE" or "STEP_REFERENCE_UNKNOWN" => "previously executed step output",
            "OPAQUE_RESPONSE_DEEP_ACCESS" or "STEP_OUTPUT_PROPERTY_UNKNOWN" => "documented output path",
            "MCP_REQUEST_SCHEMA_INVALID" => "request matching MCP input_schema",
            "MCP_CALL_INPUT_FIELD_UNKNOWN" => "supported mcp.call input envelope",
            "MCP_METHOD_UNKNOWN" => "discovered MCP method",
            "MCP_SERVER_UNKNOWN" => "discovered MCP server",
            ErrorCodes.ExprTypeMismatch => "expression result matching expected type",
            _ => null
        };
    }

    private static string BuildSemanticLlmGuidance(WorkflowSemanticValidationError error, string hint)
    {
        if (error.AllowedPaths.Count > 0)
            return $"{hint} Prefer one of allowed_paths when it satisfies the task.";

        return hint;
    }

    private static string BuildDryRunHint(string code, string message)
    {
        if (code == ErrorCodes.ExprTypeMismatch || message.Contains("requires", StringComparison.OrdinalIgnoreCase))
            return "Change the expression or declared contract so the runtime value type matches the expected type.";

        if (code is "MCP_REQUEST_SCHEMA_INVALID" or "MCP_METHOD_UNKNOWN" or "MCP_SERVER_UNKNOWN")
            return "Fix the mcp.call server, method, and input.request against the discovered MCP catalog.";

        if (message.Contains("data.steps.", StringComparison.Ordinal))
            return "Fix the step reference so it points to an earlier guaranteed step output with the documented shape.";

        return "Replay the generated workflow mentally with dry-run sample inputs and fix the first expression, input, or step dependency that cannot execute.";
    }

    private static string BuildDryRunLlmGuidance(string code, string hint)
    {
        return code switch
        {
            ErrorCodes.ExprTypeMismatch => hint + " For numeric fields, use numeric workflow inputs or structured JSON fields, not free-form LLM text.",
            "MCP_REQUEST_SCHEMA_INVALID" => hint + " Preserve exact scalar types: integers/numbers/booleans must not be quoted strings.",
            _ => hint
        };
    }

    private static string? ExtractLocationFromRuntimeDetails(JsonNode? runtimeDetails)
    {
        if (runtimeDetails is not JsonObject obj)
            return null;

        var field = GetString(obj, "field")
            ?? GetString(obj, "invalid_path")
            ?? GetString(obj, "method")
            ?? GetString(obj, "server");

        return string.IsNullOrWhiteSpace(field) ? null : field;
    }

    private static void AddExceptionInfo(JsonObject obj, Exception exception)
    {
        obj["exception_type"] = exception.GetType().Name;
        if (exception is WorkflowParseException parseEx)
        {
            obj["line"] = parseEx.Line;
            obj["column"] = parseEx.Column;
        }
    }

    private static void AddOptional(JsonObject obj, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            obj[name] = value;
    }

    private static string? GetString(JsonObject obj, string name)
    {
        return obj[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }
}
