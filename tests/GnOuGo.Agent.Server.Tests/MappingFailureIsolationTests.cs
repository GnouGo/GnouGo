using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GnOuGo.Agent.Server.Tests;

public sealed class MappingFailureIsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Yaml = """
        version: 1
        workflows:
          main:
            steps:
              - id: observe
                type: mcp.call
                input: {server: fixture, method: read, arguments: {}}
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
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappingFailureLeavesHttpAndRevisionInteractionAliveAndDoesNotRepeatCommittedWork(bool automatic)
    {
        var reads = 0; var writes = 0; var model = new Model();
        var transport = new FakeMcpClientFactory(new FakeMcpSession("fixture")
            .OnTool("read", (_, _) => { reads++; return Task.FromResult(new McpCallResult { Content = new JsonObject() }); })
            .OnTool("write", (_, _) => { writes++; return Task.FromResult(new McpCallResult { Content = new JsonObject() }); }));
        await using var host = BudgetHost.Create(Yaml, model, transport, ["--Flow:Execution:LlmBudget:MaxCalls=2",
            "--TypedWorkflowPlanning:AutomaticCorrectionEnabled=" + automatic.ToString()]);
        await host.App.StartAsync(Ct);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(host.App.Urls.First()) };
            Assert.Equal("ok", (await http.GetFromJsonAsync<JsonObject>("/health", Ct))!["status"]!.ToString());
            var input = host.App.Services.GetRequiredService<AgentHumanInputProvider>();
            var answer = automatic ? Task.CompletedTask : AnswerRevisionAsync(input, http);
            var events = new List<SmartFlowEvent>();
            await foreach (var e in host.App.Services.GetRequiredService<SmartFlowService>().ExecuteAsync("fixture", "mapping-failure", "budget-fixture", Ct)) events.Add(e);
            await answer;
            if (automatic)
            {
                Assert.DoesNotContain(events, e => e.Type == "human_input_request");
                Assert.Contains(events, e => e.Type == "answer" && e.Text!.Contains("separate revision draft", StringComparison.Ordinal));
            }
            else Assert.Single(events, e => e.Type == "human_input_request");
            var error = Assert.Single(events, e => e.Type == "error");
            Assert.Equal("CONTRACT_UNSATISFIED", error.ErrorCode);
            Assert.Contains("Result path: $/outcome/ready", error.Text);
            Assert.Contains("Mapping attempts: 2", error.Text);
            Assert.DoesNotContain("PRIVATE_OBSERVATION", error.Text);
            Assert.Equal(1, reads); Assert.Equal(0, writes); Assert.Equal(2, model.Calls);
            Assert.False(host.App.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            Assert.Equal("ok", (await http.GetFromJsonAsync<JsonObject>("/health", Ct))!["status"]!.ToString());
            var negotiate = await http.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, Ct);
            negotiate.EnsureSuccessStatusCode();
            var saved = (await host.Store.ReadAsync("local", "mapping-failure", Ct))!;
            Assert.Equal(WorkflowRunStatus.Failed, saved.Status);
            var resumed = await host.App.Services.GetRequiredService<WorkflowRunService>().CommandAsync("mapping-failure", "resume", new(saved.Revision), Ct);
            Assert.False(resumed.Result!.Success); Assert.Equal(2, model.Calls); Assert.Equal(1, reads); Assert.Equal(0, writes);
        }
        finally { await host.App.StopAsync(Ct); }
    }

    private static async Task AnswerRevisionAsync(AgentHumanInputProvider input, HttpClient http)
    {
        var request = await input.PendingRequests.ReadAsync(Ct);
        Assert.Equal("agent_workflow_revision", request.StepId);
        Assert.Contains("failed while running", request.Prompt);
        Assert.Contains("$/outcome/ready", request.Context!.ToJsonString());
        Assert.Equal("ok", (await http.GetFromJsonAsync<JsonObject>("/health", Ct))!["status"]!.ToString());
        Assert.True(await input.TrySubmitResponseAsync(request.RunId, request.StepId, JsonValue.Create("skip")));
    }

    [Fact]
    public void FormatterExplainsUnsupportedProgramWithoutExposingItsText()
    {
        var result = WorkflowFailureFormatter.Format(new() { Code = "CONTRACT_UNSATISFIED", Message = "Mapping JavaScript contains an unsupported operation.", Details = new JsonObject
        {
            ["last_error"] = new JsonObject { ["mapping_failure_kind"] = "program", ["mapping_failure_reason"] = "unsupported_operator",
                ["mapping_operator"] = "+", ["mapping_node_kind"] = "BinaryExpression", ["script_start"] = 17, ["script_length"] = 20,
                ["mapping_attempts"] = 2, ["consumer_binding"] = "extract_reference", ["script"] = "PRIVATE_SCRIPT", ["sources"] = "PRIVATE_OBSERVATION" }
        } });
        Assert.Contains("Mapping program rejected", result.UserMessage);
        Assert.Contains("Operator: +", result.UserMessage);
        Assert.Contains("Script offset (UTF-16, zero-based): 17", result.TraceDetails);
        Assert.Contains("Mapping attempts: 2", result.TraceDetails);
        Assert.DoesNotContain("PRIVATE_", result.TraceDetails);
    }

    [Fact]
    public void FormatterKeepsNestedRootDiagnosticsAndOmitsPayloads()
    {
        var result = WorkflowFailureFormatter.Format(new() { Code = "CONTRACT_UNSATISFIED", Message = "Mapping failed", Details = new JsonObject
        {
            ["last_error"] = new JsonObject { ["mapping_result_path"] = "$/outcome/ready", ["mapping_failure_kind"] = "program",
                ["mapping_failure_reason"] = "ungrounded_scalar", ["consumer_binding"] = "typed_outcome", ["mapping_attempts"] = 2,
                ["script"] = "PRIVATE_SCRIPT", ["sources"] = "PRIVATE_OBSERVATION" }
        } });
        Assert.Contains("typed_outcome", result.UserMessage); Assert.Contains("ungrounded_scalar", result.TraceDetails);
        Assert.DoesNotContain("PRIVATE_", result.TraceDetails);
    }

    private sealed class Model : ILLMClient
    {
        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; Assert.True(Calls <= 2, "No additional inference after bounded failure.");
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({outcome:{ready:m.has(source.record,'text')}})" },
                Usage = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 10, ["total_tokens"] = 20 } });
        }
    }
}
