using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Runtime.Executors;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowPlanStructuredGenerationTests
{
    private const string ValidYaml = """
        version: 1
        name: structured-generation
        skill:
          description: Generate a deterministic value.
          tags: [test]
          inputs: {}
          outputs:
            value: { type: string }
        workflows:
          main:
            steps:
              - id: value
                type: set
                input:
                  value: ok
            outputs:
              value: { expr: "${data.steps.value.value}", type: string }
        """;

    [Fact]
    public async Task BasicGeneration_UsesStrictEnvelopeAndKeepsPublicOutputUnchanged()
    {
        var client = new RecordingClient(request => BuildEnvelope(request, ValidYaml));
        var result = await ExecuteAsync(client, supportsStructuredOutput: true);

        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(client.Requests);
        Assert.True(request.StructuredOutputStrict);
        Assert.NotNull(request.StructuredOutputSchema);
        var output = Assert.IsType<JsonObject>(result.Outputs!["plan"]);
        Assert.Equal(ValidYaml.Trim(), output["yaml"]!.GetValue<string>().Trim());
        Assert.Equal(["workflow", "yaml", "meta", "diagnostics"], output.Select(static item => item.Key).ToArray());
    }

    [Fact]
    public async Task BasicGeneration_InvalidStructuredResponse_RetriesExactContractOnce()
    {
        var call = 0;
        var client = new RecordingClient(request => ++call == 1
            ? new LLMResponse { Text = ValidYaml }
            : BuildEnvelope(request, ValidYaml));

        var result = await ExecuteAsync(client, supportsStructuredOutput: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(
            client.Requests[0].StructuredOutputSchema!.ToJsonString(),
            client.Requests[1].StructuredOutputSchema!.ToJsonString());
        Assert.Equal(client.Requests[0].Prompt, client.Requests[1].Prompt);
    }

    [Fact]
    public async Task BasicGeneration_InvalidStructuredResponseTwice_FailsWithLlmSchema()
    {
        var client = new RecordingClient(_ => new LLMResponse { Text = ValidYaml });

        var result = await ExecuteAsync(client, supportsStructuredOutput: true);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task BasicGeneration_RepairEnvelopeLocksCandidateAndDiagnostics()
    {
        const string invalidYaml = "version: 1\nname: incomplete";
        var call = 0;
        var client = new RecordingClient(request => ++call == 1
            ? BuildEnvelope(request, invalidYaml)
            : BuildEnvelope(request, ValidYaml, addressDiagnostics: true));

        var result = await ExecuteAsync(client, supportsStructuredOutput: true, maxAttempts: 3);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, client.Requests.Count);
        var repairProperties = client.Requests[1].StructuredOutputSchema!["properties"]!.AsObject();
        Assert.NotEmpty(repairProperties["base_candidate_fingerprint"]!["enum"]![0]!.GetValue<string>());
        Assert.NotEmpty(repairProperties["diagnostic_fingerprint"]!["enum"]![0]!.GetValue<string>());
        Assert.NotEmpty(repairProperties["addressed_diagnostic_codes"]!["items"]!["enum"]!.AsArray());
    }

    [Fact]
    public void DiagnosticCodes_IncludeValidatedNestedDetailsForConvergence()
    {
        var exception = new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Validation failed.",
            details: new JsonObject
            {
                ["diagnostics"] = new JsonArray
                {
                    new JsonObject { ["code"] = "INPUT_SCHEMA_INVALID" },
                    new JsonObject { ["diagnostic_code"] = "STEP_REFERENCE_UNKNOWN" }
                },
                ["matching_issues"] = new JsonArray
                {
                    new JsonObject { ["issue_code"] = "CONTRACT_GAP" }
                }
            });
        var method = typeof(WorkflowPlanExecutor).GetMethod(
            "GetPlannerDiagnosticCodes",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var codes = Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(null, [exception]));

        Assert.Equal(
            ["CONTRACT_GAP", "INPUT_SCHEMA_INVALID", "STEP_REFERENCE_UNKNOWN", ErrorCodes.TemplatePlan],
            codes);

        Assert.Equal(
            ["CONTRACT_GAP", "INPUT_SCHEMA_INVALID", "STEP_REFERENCE_UNKNOWN"],
            WorkflowPlanDiagnostics.BuildDiagnosticIdentities(exception).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DiagnosticIdentities_DistinguishSameCodeAtDifferentValidatedLocations()
    {
        var exception = new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Validation failed.",
            details: new JsonObject
            {
                ["diagnostics"] = new JsonArray
                {
                    new JsonObject { ["code"] = "STEP_REFERENCE_UNKNOWN", ["location"] = "workflows.main.steps[0]" },
                    new JsonObject { ["code"] = "STEP_REFERENCE_UNKNOWN", ["location"] = "workflows.main.steps[1]" }
                }
            });

        Assert.Equal(2, WorkflowPlanDiagnostics.BuildDiagnosticIdentities(exception).Count);
    }

    [Fact]
    public void DiagnosticIdentities_DistinguishSameCodeAtDifferentLeaves()
    {
        var exception = new WorkflowRuntimeException(
            ErrorCodes.TemplatePlan,
            "Validation failed.",
            details: new JsonObject
            {
                ["diagnostics"] = new JsonArray
                {
                    new JsonObject { ["code"] = "OUTPUT_CONTRACT_INCOMPLETE", ["leaf_name"] = "first_leaf" },
                    new JsonObject { ["code"] = "OUTPUT_CONTRACT_INCOMPLETE", ["leaf_name"] = "second_leaf" }
                }
            });

        var identities = WorkflowPlanDiagnostics.BuildDiagnosticIdentities(exception);

        Assert.Equal(2, identities.Count);
        Assert.All(identities, static identity => Assert.Contains("leaf_name=", identity, StringComparison.Ordinal));
    }

    [Fact]
    public void StrictDiagnosticDecrease_RequiresAnActualSubset()
    {
        IReadOnlySet<string> baseline = new HashSet<string>(["A|leaf=one", "B|leaf=two"], StringComparer.Ordinal);

        Assert.True(WorkflowPlanDiagnostics.IsStrictDiagnosticDecrease(
            new HashSet<string>(["A|leaf=one"], StringComparer.Ordinal),
            baseline));
        Assert.False(WorkflowPlanDiagnostics.IsStrictDiagnosticDecrease(
            new HashSet<string>(["C|leaf=three"], StringComparer.Ordinal),
            baseline));
        Assert.False(WorkflowPlanDiagnostics.IsStrictDiagnosticDecrease(
            new HashSet<string>(baseline, StringComparer.Ordinal),
            baseline));
    }

    [Fact]
    public void PreferredRepairBudget_IsInAdditionToInitialCandidate_WhileLegacyLimitRemainsTotal()
    {
        var basicBudget = typeof(WorkflowPlanExecutor).GetMethod(
            "GetWorkflowPlanRepairMaxAttempts",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var pipelineBudget = typeof(WorkflowPlanExecutor).GetMethod(
            "GetPipelineGenerationMaxAttempts",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var validate = new JsonObject { ["max_repair_attempts"] = 3 };
        var legacy = new JsonObject { ["max_attempts"] = 3 };

        Assert.Equal(4, Assert.IsType<int>(basicBudget.Invoke(null, [legacy, validate])));
        Assert.Equal(3, Assert.IsType<int>(basicBudget.Invoke(null, [legacy, null])));
        Assert.Equal(4, Assert.IsType<int>(pipelineBudget.Invoke(null,
        [
            new JsonObject { ["validate"] = validate.DeepClone(), ["on_invalid"] = legacy.DeepClone() }
        ])));
    }

    [Fact]
    public async Task BasicGeneration_WrongLockedFingerprint_RetriesExactContractThenFailsAtomically()
    {
        var client = new RecordingClient(request =>
        {
            var response = BuildEnvelope(request, ValidYaml);
            response.Json!["contract_fingerprint"] = "stale-contract";
            return response;
        });

        var result = await ExecuteAsync(client, supportsStructuredOutput: true);

        Assert.False(result.Success);
        Assert.Null(result.Outputs);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task BasicGeneration_WrongAddressedDiagnosticCode_FailsClosed()
    {
        const string invalidYaml = "version: 1\nname: incomplete";
        var call = 0;
        var client = new RecordingClient(request =>
        {
            call++;
            if (call == 1)
                return BuildEnvelope(request, invalidYaml);
            var response = BuildEnvelope(request, ValidYaml, addressDiagnostics: true);
            response.Json!["addressed_diagnostic_codes"] = new JsonArray("UNDECLARED_DIAGNOSTIC");
            return response;
        });

        var result = await ExecuteAsync(client, supportsStructuredOutput: true, maxAttempts: 3);

        Assert.False(result.Success);
        Assert.Null(result.Outputs);
        Assert.Equal(ErrorCodes.LlmSchema, result.Error!.Code);
        Assert.Equal(3, client.Requests.Count);
    }

    [Fact]
    public async Task BasicGeneration_TwoUnchangedRepairs_StopAtConvergenceBoundary()
    {
        const string invalidYaml = "version: 1\nname: incomplete";
        var client = new RecordingClient(request =>
            BuildEnvelope(request, invalidYaml, addressDiagnostics: true));

        var result = await ExecuteAsync(client, supportsStructuredOutput: true, maxAttempts: 3);

        Assert.False(result.Success);
        Assert.Null(result.Outputs);
        Assert.Equal(ErrorCodes.WorkflowPlanRepairStalled, result.Error!.Code);
        Assert.Equal("candidate_unchanged", result.Error.Details!["stall_reason"]!.GetValue<string>());
        Assert.Equal(3, client.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task BasicGeneration_UnsupportedOrUnknownModel_UsesLegacyText(bool? supportsStructuredOutput)
    {
        var client = new RecordingClient(_ => new LLMResponse { Text = ValidYaml });

        var result = await ExecuteAsync(client, supportsStructuredOutput);

        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(client.Requests);
        Assert.Null(request.StructuredOutputSchema);
        Assert.Null(request.StructuredOutputStrict);
    }

    [Fact]
    public async Task PipelineGeneration_UsesStrictContractsAtEveryEligibleBoundary()
    {
        var client = new RecordingClient(request =>
        {
            if (request.Prompt.Contains("preparing a raw user automation prompt", StringComparison.Ordinal))
            {
                return new LLMResponse
                {
                    Json = new JsonObject { ["normalized_markdown"] = "# Transform\n\nTransform the query into a value." }
                };
            }

            if (request.Prompt.Contains("annotate normalized automation Markdown", StringComparison.Ordinal))
            {
                return new LLMResponse
                {
                    Json = new JsonObject
                    {
                        ["annotated_markdown"] = """
                            # Transform

                            :::subworkflow name="transform_value"
                            goal: Transform the query into a value.
                            inputs:
                              query: string
                            outputs:
                              value: string
                            extract_reason: Non-trivial reusable transformation.
                            content:
                              Transform query and expose value.
                            :::

                            ## Main workflow orchestration

                            Call transform_value and expose value.
                            """,
                        ["main_orchestration"] = "Call transform_value and expose value.",
                        ["subworkflows"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "transform_value",
                                ["goal"] = "Transform the query into a value.",
                                ["description"] = "Produce a typed value.",
                                ["work_kind"] = "deterministic_shaping",
                                ["contract_role"] = "algorithmic_transform",
                                ["concrete_outcome"] = "A typed string value.",
                                ["owned_operation_ids"] = new JsonArray(),
                                ["inputs"] = new JsonArray { StructuredField("query", "string") },
                                ["outputs"] = new JsonArray { StructuredField("value", "string") },
                                ["extract_reason"] = "Non-trivial reusable transformation.",
                                ["content"] = "Transform query and expose value.",
                                ["planned_tools"] = new JsonArray()
                            }
                        }
                    }
                };
            }

            if (request.Prompt.Contains("reviewing the quality of a `workflow.plan` pipeline", StringComparison.Ordinal))
            {
                return new LLMResponse
                {
                    Json = new JsonObject
                    {
                        ["score"] = 95,
                        ["verdict"] = "pass",
                        ["diagnostics"] = new JsonArray(),
                        ["retry_guidance"] = ""
                    }
                };
            }

            if (request.Prompt.Contains("Generate exactly one leaf GnOuGo workflow named `transform_value`", StringComparison.Ordinal))
            {
                const string leafYaml = """
                    version: 1
                    name: transform-value
                    skill:
                      description: Transform a value.
                      tags: [test]
                      inputs: { query: string }
                      outputs: { value: string }
                    workflows:
                      transform_value:
                        inputs: { query: string }
                        steps:
                          - id: result
                            type: set
                            input: { value: "${data.inputs.query}" }
                        outputs:
                          value: { expr: "${data.steps.result.value}", type: string }
                    """;
                return BuildEnvelope(request, leafYaml);
            }

            if (request.Prompt.Contains("assembling the parent `main` workflow", StringComparison.Ordinal))
            {
                return BuildMainEnvelope(
                    request,
                    """
                    name: transform-pipeline
                    skill:
                      description: Transform a value.
                      tags: [test]
                      inputs: { query: string }
                      outputs: { value: string }
                    """,
                    """
                    inputs: { query: string }
                    steps:
                      - id: call_transform_value
                        leaf: transform_value
                        args: { query: "${data.inputs.query}" }
                    outputs:
                      value: "${data.steps.call_transform_value.outputs.value}"
                    """);
            }

            throw new InvalidOperationException("Unexpected planner prompt.");
        });
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: pipeline
                      raw_prompt: Transform the query into a value.
                      generator:
                        provider: test
                        model: planner
                        prefilter: false
                      validate:
                        compile: false
                        max_repair_attempts: 1
            """);
        var compiled = new WorkflowCompiler().Compile(document);
        var engine = new WorkflowEngine
        {
            LLMClient = client,
            LLMCapabilities = new CapabilityResolver(true)
        };

        var result = await engine.ExecuteAsync(
            compiled.Workflows[compiled.Entrypoint!],
            new JsonObject(),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(5, client.Requests.Count);
        Assert.All(client.Requests, request =>
        {
            Assert.NotNull(request.StructuredOutputSchema);
            Assert.True(request.StructuredOutputStrict);
        });
    }

    private static async Task<RunResult> ExecuteAsync(
        ILLMClient client,
        bool? supportsStructuredOutput,
        int maxAttempts = 1)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      generator:
                        provider: test
                        model: planner
                        instruction: Generate a deterministic value.
                        prefilter: false
                      validate:
                        compile: false
                      on_invalid:
                        action: reprompt
                        max_attempts: 1
            """.Replace("max_attempts: 1", $"max_attempts: {maxAttempts}", StringComparison.Ordinal));
        var compiled = new WorkflowCompiler().Compile(document);
        var engine = new WorkflowEngine
        {
            LLMClient = client,
            LLMCapabilities = new CapabilityResolver(supportsStructuredOutput)
        };
        return await engine.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), CancellationToken.None);
    }

    private static LLMResponse BuildEnvelope(
        LLMRequest request,
        string yaml,
        bool addressDiagnostics = false)
    {
        var properties = request.StructuredOutputSchema!["properties"]!.AsObject();
        static string EnumValue(JsonObject properties, string name)
            => properties[name]!["enum"]![0]!.GetValue<string>();
        var addressedDiagnosticCodes = new JsonArray();
        if (addressDiagnostics
            && properties["addressed_diagnostic_codes"]!["items"]!["enum"] is JsonArray allowedCodes)
        {
            foreach (var code in allowedCodes)
                addressedDiagnosticCodes.Add(code?.DeepClone());
        }
        return new LLMResponse
        {
            Json = new JsonObject
            {
                ["schema_version"] = EnumValue(properties, "schema_version"),
                ["contract_fingerprint"] = EnumValue(properties, "contract_fingerprint"),
                ["base_candidate_fingerprint"] = EnumValue(properties, "base_candidate_fingerprint"),
                ["diagnostic_fingerprint"] = EnumValue(properties, "diagnostic_fingerprint"),
                ["addressed_diagnostic_codes"] = addressedDiagnosticCodes,
                ["yaml"] = yaml
            }
        };
    }

    private static LLMResponse BuildMainEnvelope(LLMRequest request, string documentYaml, string graphYaml)
    {
        var response = BuildEnvelope(request, "unused");
        var json = response.Json!.AsObject();
        json.Remove("yaml");
        json["document_yaml"] = documentYaml;
        json["graph_yaml"] = graphYaml;
        return response;
    }

    private static JsonObject StructuredField(string name, string type)
        => new()
        {
            ["name"] = name,
            ["type"] = type,
            ["description"] = "Typed field.",
            ["required"] = true,
            ["nullable"] = false,
            ["item_type"] = "",
            ["properties"] = new JsonArray(),
            ["enum_values"] = new JsonArray()
        };

    private sealed class CapabilityResolver(bool? supportsStructuredOutput) : ILLMCapabilityResolver
    {
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
            => Task.FromResult(supportsStructuredOutput);
    }

    private sealed class RecordingClient(Func<LLMRequest, LLMResponse> responseFactory) : ILLMClient
    {
        public List<LLMRequest> Requests { get; } = [];

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }
}
