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
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Generated workflow dry_run compilation failed: {ex.Message}");
        }

        var entrypoint = compiled.Entrypoint;
        if (string.IsNullOrWhiteSpace(entrypoint) || !compiled.Workflows.TryGetValue(entrypoint, out var workflow))
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                "Generated workflow dry_run failed: compiled workflow has no executable entrypoint.");
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
                MaxLoopIterations = 2,
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
            throw new WorkflowRuntimeException(
                ErrorCodes.TemplatePlan,
                $"Generated workflow dry_run failed before execution: {ex.Message}");
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

        throw new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            $"Generated workflow dry_run failed: [{code}] {message}");
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
            return ConvertDefaultToNode(def.Default);

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

    private static JsonNode? ConvertDefaultToNode(object value)
    {
        return value switch
        {
            JsonNode node => node.DeepClone(),
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            float f => JsonValue.Create(f),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create(m),
            System.Collections.IDictionary dict => ConvertDictionary(dict),
            System.Collections.IEnumerable enumerable when value is not string => ConvertArray(enumerable),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static JsonObject ConvertDictionary(System.Collections.IDictionary dict)
    {
        var obj = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            var key = entry.Key.ToString();
            if (!string.IsNullOrWhiteSpace(key))
                obj[key] = entry.Value == null ? null : ConvertDefaultToNode(entry.Value);
        }
        return obj;
    }

    private static JsonArray ConvertArray(System.Collections.IEnumerable enumerable)
    {
        var array = new JsonArray();
        foreach (var item in enumerable)
            array.Add(item == null ? null : ConvertDefaultToNode(item));
        return array;
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
