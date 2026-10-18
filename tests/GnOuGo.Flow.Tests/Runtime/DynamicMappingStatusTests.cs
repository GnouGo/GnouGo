using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class DynamicMappingStatusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("true", "program", "ungrounded_scalar")]
    [InlineData("m.has(source.record,'text')", "program", "ungrounded_scalar")]
    [InlineData("source.record.unknown", "data", "missing_scalar")]
    public void UnsupportedScalarReportsPrecisePathWithoutChangingGrounding(string value, string kind, string reason)
    {
        var source = JsonNode.Parse("""{"record":{"text":"PRIVATE_OBSERVATION"}}""");
        var error = Assert.Throws<WorkflowRuntimeException>(() => new JintSandbox().ExecuteMapping(
            "({'out/come':{'ready~now':[" + value + "]}})", source, Ct));
        Assert.Equal("CONTRACT_UNSATISFIED", error.Code); Assert.False(error.Retryable);
        Assert.Equal("$/out~1come/ready~0now/0", error.Details!["mapping_result_path"]!.ToString());
        Assert.Equal(kind, error.Details["mapping_failure_kind"]!.ToString());
        Assert.Equal(reason, error.Details["mapping_failure_reason"]!.ToString());
        Assert.DoesNotContain("PRIVATE_OBSERVATION", error.Details.ToJsonString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedBooleanRemainsExact(bool ready)
    {
        var result = new JintSandbox().ExecuteMapping("({outcome:{ready:source.ready}})",
            new JsonObject { ["ready"] = ready }, Ct);
        Assert.Equal(ready, result!["outcome"]!["ready"]!.GetValue<bool>());
    }

    [Fact]
    public async Task BoundedFailureRetainsRootDiagnosticsAndReceiptsWithoutEffectsOrNewAttempts()
    {
        var doc = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: facts
                    type: mapping.dynamic
                    input:
                      sources: {record: {text: PRIVATE_OBSERVATION}}
                      objective: Copy observed facts.
                      binding: extract_facts
                      producer_contract: fixture
                    output_schema:
                      type: object
                      properties:
                        value:
                          type: object
                          properties:
                            outcome: {type: object, properties: {ready: {type: boolean}}, required: [ready]}
                          required: [outcome]
                      required: [value]
                  - id: write
                    type: mcp.call
                    input: {server: fixture, method: write, arguments: {}}
            """);
        var model = new Model(); var store = new InMemoryWorkflowRunStore();
        var telemetry = new WorkflowTelemetryTests.RecordingTelemetry();
        var workflow = new WorkflowCompiler().Compile(doc).Workflows["main"];
        var engine = new WorkflowEngine { RunStore = store, LLMClient = model, Telemetry = telemetry,
            LlmDefaults = new() { Model = "offline" }, Limits = new() { TenantId = "fixture", RunId = "mapping-status" } };
        var result = await engine.ExecuteAsync(workflow, new JsonObject(), Ct);
        Assert.False(result.Success); Assert.Single(result.StepResults); Assert.Equal(2, model.Calls);
        Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code);
        Assert.Equal("$/outcome/ready", result.Error.Details!["mapping_result_path"]!.ToString());
        Assert.Equal("program", result.Error.Details["mapping_failure_kind"]!.ToString());
        Assert.Equal("extract_facts", result.Error.Details["consumer_binding"]!.ToString());
        Assert.Equal(2, result.Error.Details["mapping_attempts"]!.GetValue<int>());
        Assert.DoesNotContain("PRIVATE_OBSERVATION", result.Error.Details.ToJsonString());
        var attributes = Assert.Single(telemetry.StepSpans).Attributes;
        Assert.Equal("$/outcome/ready", attributes["gnougo.mapping.result_path"]);
        var saved = (await store.ReadAsync("fixture", "mapping-status", Ct))!;
        Assert.Equal(2, saved.Invocations.Values.Count(i => i.StepType == "llm.call" && i.CompletedAt is not null));
        var resumed = await new WorkflowEngine { RunStore = store }.ResumeAsync("fixture", "mapping-status", saved.Revision, workflow, Ct);
        Assert.False(resumed.Success); Assert.Equal(result.Error.Message, resumed.Error!.Message); Assert.Equal(2, model.Calls);
    }

    private sealed class Model : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium", "low"]);

        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({outcome:{ready:m.has(source.record,'text')}})" } });
        }
    }
}
