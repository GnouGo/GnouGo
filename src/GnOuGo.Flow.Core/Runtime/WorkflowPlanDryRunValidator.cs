using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// Executes a generated workflow once with deterministic fake providers to catch
/// runtime input-resolution issues before workflow.plan accepts the YAML.
/// </summary>
internal static class WorkflowPlanDryRunValidator
{
    private const int DryRunLoopIterationLimit = 10;

    public static async Task ValidateAsync(
        WorkflowDocument generatedDoc,
        IMcpClientFactory? mcpClientFactory,
        ILogger? logger,
        CancellationToken ct)
    {
        CompiledDocument compiled;
        try
        {
            compiled = new WorkflowCompiler().Compile(generatedDoc);
        }
        catch (Exception ex)
        {
            var details = ex is WorkflowCompilationException compilationException
                ? WorkflowPlanDiagnostics.BuildValidationFailureDetails(
                    compilationException.Errors,
                    semanticException: null,
                    compilationException,
                    phase: "dry_run_compilation")
                : WorkflowPlanDiagnostics.BuildDryRunFailureDetails(
                    "DRY_RUN_COMPILATION_FAILED",
                    ex.Message,
                    "compilation",
                    ex);

            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Generated workflow dry_run compilation failed: {ex.Message} | repair diagnostics: {WorkflowPlanDiagnostics.ToPromptJson(details)}",
                inner: ex,
                details: details);
        }

        var entrypoint = compiled.Entrypoint;
        if (string.IsNullOrWhiteSpace(entrypoint) || !compiled.Workflows.TryGetValue(entrypoint, out var workflow))
        {
            var details = WorkflowPlanDiagnostics.BuildDryRunFailureDetails(
                "DRY_RUN_ENTRYPOINT_MISSING",
                "compiled workflow has no executable entrypoint.",
                "entrypoint");

            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                "Generated workflow dry_run failed: compiled workflow has no executable entrypoint. | repair diagnostics: "
                + WorkflowPlanDiagnostics.ToPromptJson(details),
                details: details);
        }

        var engine = new WorkflowEngine
        {
            LLMClient = new DryRunLlmClient(),
            McpClientFactory = mcpClientFactory,
            HumanInputProvider = new DryRunHumanInputProvider(),
            WorkflowCandidateProvider = DryRunWorkflowCandidateProvider.Instance,
            LlmDefaults = new LlmRuntimeDefaults { Model = "dry-run-model" },
            Logger = logger ?? NullLogger.Instance,
            Limits = new ExecutionLimits
            {
                MaxTotalStepsExecuted = 250,
                // One-item samples keep collection loops cheap, while ten iterations allow
                // ordinary bounded retry/backoff workflows to demonstrate termination.
                MaxLoopIterations = DryRunLoopIterationLimit,
                MaxParallelBranches = 10,
                MaxCallDepth = 10,
                RunId = "workflow-plan-dry-run"
            }
        };

        var inputs = WorkflowInputDefaults.Apply(workflow.Source, BuildSampleInputs(workflow.Source.Inputs));

        RunResult result;
        try
        {
            result = await engine.ExecuteAsync(workflow, inputs, ct);
        }
        catch (Exception ex)
        {
            var details = WorkflowPlanDiagnostics.BuildDryRunFailureDetails(
                "DRY_RUN_BEFORE_EXECUTION_FAILED",
                ex.Message,
                "before_execution",
                ex,
                ex is WorkflowRuntimeException workflowEx ? workflowEx.Details : null);

            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Generated workflow dry_run failed before execution: {ex.Message} | repair diagnostics: {WorkflowPlanDiagnostics.ToPromptJson(details)}",
                inner: ex,
                details: details);
        }

        if (result.Success)
            return;

        var error = result.Error;
        var code = string.IsNullOrWhiteSpace(error?.Code) ? "UNKNOWN" : error.Code;
        var message = string.IsNullOrWhiteSpace(error?.Message) ? "No error message returned." : error.Message;

        if (IsInconclusiveInternalError(code))
        {
            logger?.LogWarning(
                "Generated workflow dry_run was inconclusive because the dry-run runtime raised an internal error: {DryRunErrorMessage}",
                message);
            return;
        }

        var failureDetails = WorkflowPlanDiagnostics.BuildDryRunFailureDetails(
            code,
            message,
            "execution",
            runtimeDetails: error?.Details);

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Generated workflow dry_run failed: [{code}] {message} | repair diagnostics: {WorkflowPlanDiagnostics.ToPromptJson(failureDetails)}",
            details: failureDetails);
    }

    private static bool IsInconclusiveInternalError(string code) =>
        string.Equals(code, "INTERNAL_ERROR", StringComparison.Ordinal);

    internal static JsonNode? CreateSampleFromJsonSchema(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return JsonValue.Create("dry-run");

        if (TryGetFirstSchema(obj, "anyOf", out var anyOfSample)
            || TryGetFirstSchema(obj, "oneOf", out anyOfSample)
            || TryGetFirstSchema(obj, "allOf", out anyOfSample))
        {
            return anyOfSample;
        }

        if (obj["enum"] is JsonArray enumValues && enumValues.Count > 0)
            return enumValues[0]?.DeepClone();

        var type = ReadSchemaType(obj);
        return type switch
        {
            "object" => CreateSampleObject(obj),
            "array" => CreateSampleArray(obj),
            "integer" => JsonValue.Create(1),
            "number" => JsonValue.Create(1.25),
            "boolean" => JsonValue.Create(true),
            "null" => null,
            "string" => JsonValue.Create("dry-run"),
            _ => obj.ContainsKey("properties")
                ? CreateSampleObject(obj)
                : JsonValue.Create("dry-run")
        };
    }

    internal static JsonNode? CreateSuccessfulMcpSampleFromJsonSchema(JsonNode? schema)
    {
        var sample = CreateSampleFromJsonSchema(schema);
        if (sample is JsonObject obj && schema is JsonObject schemaObj)
            NormalizeSuccessfulMcpEnvelope(obj, schemaObj);

        return sample;
    }

    private static JsonObject BuildSampleInputs(Dictionary<string, InputDef>? inputs)
    {
        var sample = new JsonObject();
        if (inputs == null)
            return sample;

        foreach (var (name, def) in inputs)
            sample[name] = CreateSampleFromInputDef(def);

        return sample;
    }

    private static JsonNode? CreateSampleFromInputDef(InputDef? def)
    {
        if (def == null)
            return JsonValue.Create("dry-run");

        if (def.Default != null)
            return InputDefaultValueConverter.ConvertToNode(def.Default, def);

        return (def.Type ?? "any").Trim().ToLowerInvariant() switch
        {
            "string" or "text" or "markdown" or "yaml" or "json" or "url" or "email" or "date" or "file" or "directory"
                => JsonValue.Create("dry-run"),
            "integer" => JsonValue.Create(1),
            "number" => JsonValue.Create(1.25),
            "boolean" or "bool" => JsonValue.Create(true),
            "array" => new JsonArray(CreateSampleFromInputDef(def.Items)),
            "object" => CreateSampleObjectFromInputDef(def),
            "dictionary" => new JsonObject { ["key"] = CreateSampleFromInputDef(def.AdditionalProperties) },
            _ => JsonValue.Create("dry-run")
        };
    }

    private static JsonObject CreateSampleObjectFromInputDef(InputDef def)
    {
        var obj = new JsonObject();
        if (def.Properties == null)
            return obj;

        foreach (var (name, propertyDef) in def.Properties)
            obj[name] = CreateSampleFromInputDef(propertyDef);

        return obj;
    }

    private static bool TryGetFirstSchema(JsonObject obj, string keyword, out JsonNode? sample)
    {
        sample = null;
        if (obj[keyword] is not JsonArray schemas)
            return false;

        foreach (var schema in schemas.OfType<JsonObject>())
        {
            if (string.Equals(ReadSchemaType(schema), "null", StringComparison.OrdinalIgnoreCase))
                continue;

            sample = CreateSampleFromJsonSchema(schema);
            return true;
        }

        return false;
    }

    private static string? ReadSchemaType(JsonObject obj)
    {
        if (obj["type"] is JsonValue value)
            return value.GetValue<string>();

        if (obj["type"] is JsonArray types)
        {
            foreach (var typeNode in types.OfType<JsonValue>())
            {
                var type = typeNode.GetValue<string>();
                if (!string.Equals(type, "null", StringComparison.OrdinalIgnoreCase))
                    return type;
            }
        }

        return null;
    }

    private static JsonObject CreateSampleObject(JsonObject schema)
    {
        var obj = new JsonObject();
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, propertySchema) in properties)
                obj[name] = CreateSampleFromJsonSchema(propertySchema);
        }
        return obj;
    }

    private static void NormalizeSuccessfulMcpEnvelope(JsonObject sample, JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
            return;

        var hasSuccessMarker = false;
        if (TryGetProperty(sample, "success", out var successKey))
        {
            sample[successKey] = JsonValue.Create(true);
            hasSuccessMarker = true;
        }

        if (TryGetProperty(sample, "ok", out var okKey))
        {
            sample[okKey] = JsonValue.Create(true);
            hasSuccessMarker = true;
        }

        if (TryGetProperty(sample, "status", out var statusKey)
            && TryGetPropertySchema(properties, statusKey, out var statusSchema)
            && TryCreateSuccessfulStatusValue(statusSchema, out var statusValue))
        {
            sample[statusKey] = statusValue;
            hasSuccessMarker = true;
        }

        if (!hasSuccessMarker)
            return;

        foreach (var property in properties)
        {
            if (!TryGetProperty(sample, property.Key, out var sampleKey))
                continue;

            if (!IsMcpEnvelopeErrorProperty(property.Key, sample))
                continue;

            SetNeutralErrorSampleValue(sample, sampleKey, property.Value, schema);
        }
    }

    private static bool TryCreateSuccessfulStatusValue(JsonNode? schema, out JsonNode value)
    {
        value = JsonValue.Create("ok");
        if (schema is not JsonObject obj)
            return true;

        if (obj["enum"] is JsonArray enumValues && enumValues.Count > 0)
        {
            foreach (var preferred in new[] { "ok", "success", "succeeded", "done", "completed" })
            {
                foreach (var enumValue in enumValues.OfType<JsonValue>())
                {
                    if (enumValue.TryGetValue<string>(out var text)
                        && string.Equals(text, preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        value = JsonValue.Create(text);
                        return true;
                    }
                }
            }

            return false;
        }

        var type = ReadSchemaType(obj);
        return string.IsNullOrWhiteSpace(type) || string.Equals(type, "string", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetNeutralErrorSampleValue(JsonObject sample, string key, JsonNode? propertySchema, JsonObject objectSchema)
    {
        if (SchemaAcceptsNull(propertySchema))
        {
            sample[key] = null;
            return;
        }

        if (!IsRequiredProperty(objectSchema, key))
        {
            sample.Remove(key);
            return;
        }

        if (propertySchema is JsonObject schemaObj
            && string.Equals(ReadSchemaType(schemaObj), "string", StringComparison.OrdinalIgnoreCase))
        {
            sample[key] = JsonValue.Create(string.Empty);
            return;
        }

        sample[key] = null;
    }

    private static bool SchemaAcceptsNull(JsonNode? schema)
    {
        if (schema is not JsonObject obj)
            return true;

        if (string.Equals(ReadSchemaTypeIncludingNull(obj), "null", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (obj[keyword] is not JsonArray schemas)
                continue;

            foreach (var candidate in schemas.OfType<JsonObject>())
            {
                if (SchemaAcceptsNull(candidate))
                    return true;
            }
        }

        return false;
    }

    private static string? ReadSchemaTypeIncludingNull(JsonObject obj)
    {
        if (obj["type"] is JsonValue value)
            return value.GetValue<string>();

        if (obj["type"] is JsonArray types)
        {
            foreach (var typeNode in types.OfType<JsonValue>())
            {
                var type = typeNode.GetValue<string>();
                if (string.Equals(type, "null", StringComparison.OrdinalIgnoreCase))
                    return type;
            }
        }

        return null;
    }

    private static bool IsRequiredProperty(JsonObject schema, string propertyName)
    {
        if (schema["required"] is JsonArray required
            && required.OfType<JsonValue>().Any(value =>
                value.TryGetValue<string>(out var text)
                && string.Equals(text, propertyName, StringComparison.Ordinal)))
        {
            return true;
        }

        if (schema["required_properties"] is JsonArray requiredProperties
            && requiredProperties.OfType<JsonValue>().Any(value =>
                value.TryGetValue<string>(out var text)
                && string.Equals(text, propertyName, StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static bool IsMcpEnvelopeErrorProperty(string key, JsonObject sample)
    {
        if (EqualsAnyIgnoreCase(key, "error", "errorCode", "error_code", "errorMessage", "error_message"))
            return true;

        if (string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
            return TryGetProperty(sample, "message", out _)
                || TryGetProperty(sample, "errorMessage", out _)
                || TryGetProperty(sample, "error_message", out _);

        if (string.Equals(key, "message", StringComparison.OrdinalIgnoreCase))
            return TryGetProperty(sample, "code", out _)
                || TryGetProperty(sample, "errorCode", out _)
                || TryGetProperty(sample, "error_code", out _);

        return false;
    }

    private static bool EqualsAnyIgnoreCase(string value, params string[] candidates)
        => candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetProperty(JsonObject obj, string name, out string actualName)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                actualName = property.Key;
                return true;
            }
        }

        actualName = string.Empty;
        return false;
    }

    private static bool TryGetPropertySchema(JsonObject properties, string name, out JsonNode? schema)
    {
        foreach (var property in properties)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                schema = property.Value;
                return true;
            }
        }

        schema = null;
        return false;
    }

    private static JsonArray CreateSampleArray(JsonObject schema)
    {
        var array = new JsonArray();
        array.Add(CreateSampleFromJsonSchema(schema["items"]));
        return array;
    }

    private sealed class DryRunLlmClient : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var json = request.StructuredOutputSchema == null
                ? null
                : CreateSampleFromJsonSchema(request.StructuredOutputSchema);

            return Task.FromResult(new LLMResponse
            {
                Text = json?.ToJsonString() ?? "dry-run text response",
                Json = json?.DeepClone(),
                Usage = new JsonObject
                {
                    ["prompt_tokens"] = 1,
                    ["completion_tokens"] = 1,
                    ["total_tokens"] = 2
                }
            });
        }
    }

    private sealed class DryRunHumanInputProvider : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (request.Fields is { Count: > 0 })
            {
                var obj = new JsonObject();
                foreach (var field in request.Fields)
                    obj[field.Name] = CreateSampleFromHumanField(field);

                return Task.FromResult<JsonNode?>(obj);
            }

            if (string.Equals(request.Mode, HumanInputContract.ModeConfirm, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<JsonNode?>(JsonValue.Create(true));

            if (request.Choices is { Count: > 0 })
                return Task.FromResult<JsonNode?>(JsonValue.Create(request.Choices[0]));

            return Task.FromResult<JsonNode?>(JsonValue.Create("dry-run human response"));
        }

        private static JsonNode? CreateSampleFromHumanField(HumanInputFieldDef field)
        {
            if (!string.IsNullOrWhiteSpace(field.Default))
                return JsonValue.Create(field.Default);

            var type = field.Type.Trim().ToLowerInvariant();
            if (field.Options is { Count: > 0 }
                && (type is "select" or "radio" or "multiselect" or "checkbox"))
            {
                if (type is "multiselect" or "checkbox")
                    return new JsonArray(JsonValue.Create(field.Options[0]));

                return JsonValue.Create(field.Options[0]);
            }

            return type switch
            {
                "number" => JsonValue.Create(1.25),
                "integer" => JsonValue.Create(1),
                "boolean" => JsonValue.Create(true),
                "json" => new JsonObject { ["value"] = JsonValue.Create("dry-run") },
                _ => JsonValue.Create("dry-run")
            };
        }
    }

    private sealed class DryRunWorkflowCandidateProvider : IWorkflowCandidateProvider
    {
        public static readonly DryRunWorkflowCandidateProvider Instance = new();

        public Task<IReadOnlyList<WorkflowRouteCandidate>> GetCandidatesAsync(
            WorkflowRouteCandidateQuery query,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WorkflowRouteCandidate>>(Array.Empty<WorkflowRouteCandidate>());
        }
    }
}
