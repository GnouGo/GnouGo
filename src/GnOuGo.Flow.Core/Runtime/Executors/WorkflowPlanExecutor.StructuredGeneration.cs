using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

public sealed partial class WorkflowPlanExecutor
{
    private const string PlannerResponseSchemaVersion = "workflow-plan-response-v1";
    private static readonly ConditionalWeakTable<object, PlannerStructuredOutputEvidence> PlannerStructuredOutputEvidenceByEngine = new();

    private sealed class PlannerStructuredOutputEvidence
    {
        private readonly HashSet<string> _targets = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public bool Contains(string key)
        {
            lock (_gate)
                return _targets.Contains(key);
        }

        public void Add(string key)
        {
            lock (_gate)
                _targets.Add(key);
        }
    }

    private static async Task<bool> ShouldUseStrictPlannerResponseAsync(
        StepExecutionContext ctx,
        string? provider,
        string model,
        CancellationToken ct)
    {
        var key = BuildPlannerTargetKey(provider, model);
        var evidence = PlannerStructuredOutputEvidenceByEngine.GetOrCreateValue(ctx.Engine);
        if (evidence.Contains(key))
            return true;

        if (ctx.Engine.LLMCapabilities == null)
            return false;

        try
        {
            return await ctx.Engine.LLMCapabilities.SupportsStructuredOutputAsync(provider, model, ct) == true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Engine.Logger.LogWarning(
                ex,
                "workflow.plan could not resolve structured-output support for the selected target; using the legacy response contract");
            return false;
        }
    }

    private static string BuildPlannerTargetKey(string? provider, string model)
        => $"{provider?.Trim().ToLowerInvariant() ?? "(default)"}\n{model.Trim().ToLowerInvariant()}";

    private static void RecordPlannerStructuredOutputProof(
        StepExecutionContext ctx,
        string? provider,
        string model,
        JsonNode? responseJson,
        JsonNode schema)
    {
        if (responseJson == null || JsonSchemaContractValidator.ValidateInstance(responseJson, schema).Count != 0)
            return;

        PlannerStructuredOutputEvidenceByEngine
            .GetOrCreateValue(ctx.Engine)
            .Add(BuildPlannerTargetKey(provider, model));
    }

    private static async Task<LLMResponse> ExecuteStrictPlannerResponseAsync(
        ILLMClient llmClient,
        string phase,
        string prompt,
        string? provider,
        string model,
        string? reasoning,
        StepExecutionContext ctx,
        CancellationToken ct,
        JsonNode schema,
        int phaseAttempt,
        int? maxAttempts,
        string contractFingerprint,
        string baseCandidateFingerprint,
        string diagnosticFingerprint,
        int contractEpoch)
    {
        var schemaErrors = JsonSchemaContractValidator.ValidateSchema(schema, strictProfile: true);
        if (schemaErrors.Count != 0)
        {
            throw new WorkflowRuntimeException(
                ErrorCodes.LlmSchema,
                $"workflow.plan internal response schema for phase '{phase}' is invalid: {string.Join("; ", schemaErrors)}");
        }

        for (var contractAttempt = 1; contractAttempt <= 2; contractAttempt++)
        {
            using var span = ctx.BeginTelemetrySpan($"workflow.plan.{phase}.structured_response", "structured_response", new[]
            {
                new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                new KeyValuePair<string, object?>("gen_ai.system", provider ?? "unspecified"),
                new KeyValuePair<string, object?>("gen_ai.request.model", model),
                new KeyValuePair<string, object?>("gnougo-flow.plan.response_mode", "structured"),
                new KeyValuePair<string, object?>("gnougo-flow.plan.response_schema_version", PlannerResponseSchemaVersion),
                new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase),
                new KeyValuePair<string, object?>("gnougo-flow.plan.attempt", phaseAttempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.response_contract_attempt", contractAttempt),
                new KeyValuePair<string, object?>("gnougo-flow.plan.contract_epoch", contractEpoch),
                new KeyValuePair<string, object?>("gnougo-flow.plan.contract_fingerprint", contractFingerprint),
                new KeyValuePair<string, object?>("gnougo-flow.plan.base_candidate_fingerprint", baseCandidateFingerprint),
                new KeyValuePair<string, object?>("gnougo-flow.plan.diagnostic_fingerprint", diagnosticFingerprint)
            });
            if (maxAttempts.HasValue)
                span.SetAttribute("gnougo-flow.plan.max_attempts", maxAttempts.Value);

            try
            {
                var response = await ctx.CallLLMAsync(llmClient, new LLMRequest
                {
                    Provider = provider,
                    Model = model,
                    Prompt = prompt,
                    Reasoning = reasoning,
                    UseBackgroundMode = true,
                    StructuredOutputSchema = schema.DeepClone(),
                    StructuredOutputStrict = true
                }, $"workflow.plan.{phase}", ct);

                AddUsageAttributes(span, response.Usage, model, provider);
                var validationErrors = response.Json == null
                    ? new List<string> { "$: response did not contain parsed JSON" }
                    : JsonSchemaContractValidator.ValidateInstance(response.Json, schema).ToList();
                if (response.Json is JsonObject responseObject
                    && responseObject["addressed_diagnostic_codes"] is JsonArray addressedCodes)
                {
                    var values = addressedCodes
                        .OfType<JsonValue>()
                        .Select(static value => value.TryGetValue<string>(out var code) ? code : string.Empty)
                        .ToArray();
                    if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                        validationErrors.Add("$.addressed_diagnostic_codes: values must be unique");
                }
                if (validationErrors.Count == 0)
                {
                    RecordPlannerStructuredOutputProof(ctx, provider, model, response.Json, schema);
                    span.SetAttribute("gnougo-flow.plan.response_contract_status", "valid");
                    span.Complete();
                    return response;
                }

                span.SetAttribute("gnougo-flow.plan.response_contract_status", "invalid");
                span.SetAttribute("gnougo-flow.plan.response_contract_error_count", validationErrors.Count);
                if (contractAttempt == 1)
                {
                    span.AddEvent("gnougo-flow.plan.structured_response.retry", new[]
                    {
                        new KeyValuePair<string, object?>("gnougo-flow.plan.phase", phase),
                        new KeyValuePair<string, object?>("gnougo-flow.plan.response_schema_version", PlannerResponseSchemaVersion),
                        new KeyValuePair<string, object?>("gnougo-flow.plan.response_contract_error_count", validationErrors.Count)
                    });
                    continue;
                }

                throw new WorkflowRuntimeException(
                    ErrorCodes.LlmSchema,
                    $"workflow.plan phase '{phase}' returned JSON that did not satisfy its strict internal response contract after one exact retry: {string.Join("; ", validationErrors.Take(8))}");
            }
            catch (Exception ex)
            {
                span.Fail(ex);
                throw;
            }
        }

        throw new InvalidOperationException("Unreachable structured workflow response retry state.");
    }

    private static JsonNode BuildNormalizationResponseSchema()
        => BuildStrictObjectSchema(new JsonObject
        {
            ["normalized_markdown"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
        });

    private static JsonNode BuildWorkflowGenerationResponseSchema(
        string contractFingerprint,
        string baseCandidateFingerprint,
        string diagnosticFingerprint,
        IReadOnlyList<string> diagnosticCodes,
        bool mainAssembly)
    {
        var properties = BuildPlannerEnvelopeProperties(
            contractFingerprint,
            baseCandidateFingerprint,
            diagnosticFingerprint,
            diagnosticCodes);
        if (mainAssembly)
        {
            properties["document_yaml"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 };
            properties["graph_yaml"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 };
        }
        else
        {
            properties["yaml"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 };
        }

        return BuildStrictObjectSchema(properties);
    }

    private static string AppendPlannerGenerationEnvelopeInstruction(
        string prompt,
        string contractFingerprint,
        string baseCandidateFingerprint,
        string diagnosticFingerprint,
        IReadOnlyList<string> diagnosticCodes)
    {
        var addressed = diagnosticCodes.Count == 0
            ? "an empty array"
            : "a non-empty subset of: " + string.Join(", ", diagnosticCodes);
        return prompt.TrimEnd() + $$"""


            Internal response contract:
            - Return only the strict JSON object selected by the response schema, not raw YAML or Markdown fences.
            - Put the complete workflow YAML in `yaml`.
            - Echo schema_version `{{PlannerResponseSchemaVersion}}`.
            - Echo contract_fingerprint `{{contractFingerprint}}`.
            - Echo base_candidate_fingerprint `{{baseCandidateFingerprint}}`.
            - Echo diagnostic_fingerprint `{{diagnosticFingerprint}}`.
            - addressed_diagnostic_codes must be {{addressed}}.
            - These envelope values are immutable acknowledgements; they do not override deterministic validation.
            """;
    }

    private static JsonObject BuildPlannerEnvelopeProperties(
        string contractFingerprint,
        string baseCandidateFingerprint,
        string diagnosticFingerprint,
        IReadOnlyList<string> diagnosticCodes)
    {
        var addressedItems = diagnosticCodes.Count == 0
            ? new JsonObject { ["type"] = "string" }
            : new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(diagnosticCodes.Select(static code => (JsonNode)JsonValue.Create(code)!).ToArray())
            };
        var addressed = new JsonObject
        {
            ["type"] = "array",
            ["items"] = addressedItems,
            ["maxItems"] = diagnosticCodes.Count
        };
        if (diagnosticCodes.Count == 0)
            addressed["minItems"] = 0;
        else
            addressed["minItems"] = 1;

        return new JsonObject
        {
            ["schema_version"] = SingleStringEnum(PlannerResponseSchemaVersion),
            ["contract_fingerprint"] = SingleStringEnum(contractFingerprint),
            ["base_candidate_fingerprint"] = SingleStringEnum(baseCandidateFingerprint),
            ["diagnostic_fingerprint"] = SingleStringEnum(diagnosticFingerprint),
            ["addressed_diagnostic_codes"] = addressed
        };
    }

    private static JsonNode BuildStrictObjectSchema(JsonObject properties)
        => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(static item => (JsonNode)JsonValue.Create(item.Key)!).ToArray()),
            ["additionalProperties"] = false
        };

    private static JsonObject SingleStringEnum(string value)
        => new()
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(value)
        };

    private static string ReadRequiredPlannerResponseString(LLMResponse response, string property, string phase)
    {
        if (response.Json is JsonObject obj
            && obj[property] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new WorkflowRuntimeException(
            ErrorCodes.LlmSchema,
            $"workflow.plan phase '{phase}' did not return required internal field '{property}'.");
    }

    private static string ComposeStructuredMainAssemblyResponse(LLMResponse response)
    {
        var document = StripMarkdownFences(
            ReadRequiredPlannerResponseString(response, "document_yaml", "main assembly")).Trim();
        var graph = StripMarkdownFences(
            ReadRequiredPlannerResponseString(response, "graph_yaml", "main assembly")).Trim();
        return $"document:\n{IndentPlannerYaml(document)}\ngraph:\n{IndentPlannerYaml(graph)}";
    }

    private static string IndentPlannerYaml(string yaml)
        => string.Join(
            '\n',
            yaml.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(static line => "  " + line));

    private static string BuildPlannerFingerprint(params string?[] values)
    {
        var payload = string.Join("\n\u001f\n", values.Select(static value => value ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static IReadOnlyList<string> GetPlannerDiagnosticCodes(Exception exception)
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is WorkflowRuntimeException workflowException
                && !string.IsNullOrWhiteSpace(workflowException.Code))
            {
                codes.Add(workflowException.Code);
            }
        }

        if (codes.Count == 0)
            codes.Add(ErrorCodes.TemplatePlan);
        return codes.ToArray();
    }
}
