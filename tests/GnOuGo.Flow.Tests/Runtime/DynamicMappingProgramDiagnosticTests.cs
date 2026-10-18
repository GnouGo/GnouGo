using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class DynamicMappingProgramDiagnosticTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Concatenation = "({reference:source.reference + '.suffix'})";

    [Theory]
    [InlineData("({reference:source.reference + '.suffix'})", "+")]
    [InlineData("({reference:source.reference - 1})", "-")]
    public void UnsupportedOperatorsRetainTheirClassificationAndExactRange(string script, string operation)
    {
        var error = Assert.Throws<WorkflowRuntimeException>(() => JintSandbox.ValidateMapping(script));
        Assert.Equal("CONTRACT_UNSATISFIED", error.Code);
        Assert.Equal("Mapping JavaScript contains an unsupported operation.", error.Message);
        Assert.Equal("program", error.Details!["mapping_failure_kind"]!.ToString());
        Assert.Equal("unsupported_operator", error.Details["mapping_failure_reason"]!.ToString());
        Assert.Equal(operation, error.Details["mapping_operator"]!.ToString());
        Assert.Equal("BinaryExpression", error.Details["mapping_node_kind"]!.ToString());
        var start = error.Details["script_start"]!.GetValue<int>();
        var length = error.Details["script_length"]!.GetValue<int>();
        Assert.Equal(script.IndexOf("source.reference", StringComparison.Ordinal), start);
        Assert.Equal(script.IndexOf('}', start) - start, length);
        Assert.DoesNotContain("suffix", error.Details.ToJsonString());
        Assert.DoesNotContain("source.reference", error.Details.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairAndFinalErrorReceiveStructuralDetailsWithoutExtraAttempts(bool regexFirst)
    {
        const string regex = """({reference:m.text(source.reference,'(\S+)')})""";
        var model = new Model(regexFirst ? regex : Concatenation, Concatenation);
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: extract
                    type: mapping.dynamic
                    input:
                      sources: {reference: PRIVATE_OBSERVATION}
                      objective: Extract the observed reference.
                      binding: reference
                      producer_contract: fixture
                    output_schema:
                      type: object
                      properties:
                        value:
                          type: object
                          properties: {reference: {type: string}}
                          required: [reference]
                      required: [value]
                  - id: downstream
                    type: mcp.call
                    input: {server: fixture, method: write, arguments: {}}
            """);
        var workflow = new WorkflowCompiler().Compile(document).Workflows["main"];
        var store = new InMemoryWorkflowRunStore();
        var telemetry = new WorkflowTelemetryTests.RecordingTelemetry();
        var engine = new WorkflowEngine { RunStore = store, LLMClient = model, Telemetry = telemetry,
            LlmDefaults = new() { Model = "offline" }, Limits = new() { TenantId = "fixture", RunId = "program-failure" } };
        var result = await engine.ExecuteAsync(workflow, new JsonObject(), Ct);
        Assert.False(result.Success); Assert.Single(result.StepResults); Assert.Equal(2, model.Requests.Count);
        var repair = model.Requests[1].Prompt;
        var payload = JsonNode.Parse(repair[(repair.IndexOf("\n{\"objective\"", StringComparison.Ordinal) + 1)..])!;
        var details = payload["failure_details"]!;
        Assert.Equal(regexFirst ? "lost_regex_escape" : "unsupported_operator", details["mapping_failure_reason"]!.ToString());
        Assert.NotNull(details["script_start"]);
        Assert.DoesNotContain("PRIVATE_OBSERVATION", details.ToJsonString());
        Assert.Equal("+", result.Error!.Details!["mapping_operator"]!.ToString());
        Assert.Equal("unsupported_operator", result.Error.Details["mapping_failure_reason"]!.ToString());
        Assert.Equal(2, result.Error.Details["mapping_attempts"]!.GetValue<int>());
        Assert.Equal("+", Assert.Single(telemetry.StepSpans).Attributes["gnougo.mapping.operator"]);
        Assert.DoesNotContain("PRIVATE_OBSERVATION", result.Error.Details.ToJsonString());
        Assert.Empty(engine.MappingMemory);
        var saved = (await store.ReadAsync("fixture", "program-failure", Ct))!;
        Assert.Equal(2, saved.Invocations.Values.Count(i => i.StepType == "llm.call" && i.CompletedAt is not null));
        var resumed = await new WorkflowEngine { RunStore = store }.ResumeAsync("fixture", saved.RunId, saved.Revision, workflow, Ct);
        Assert.False(resumed.Success); Assert.True(JsonNode.DeepEquals(result.Error.Details, resumed.Error!.Details));
        Assert.Equal(2, model.Requests.Count);
    }

    private sealed class Model(params string[] scripts) : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium", "low"]);

        public List<LLMRequest> Requests { get; } = [];
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var script = scripts[Requests.Count]; Requests.Add(request);
            Assert.True(request.StructuredOutputStrict);
            Assert.Contains("Never fabricate scalars or concatenate strings", request.Prompt);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = script } });
        }
    }
}
