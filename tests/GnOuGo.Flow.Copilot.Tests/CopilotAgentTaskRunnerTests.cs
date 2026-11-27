using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Copilot.Tests;

public sealed class CopilotAgentTaskRunnerTests
{
    [Fact]
    public void HostSelectsExecutorBeforeAnyModelOrToolDispatch()
    {
        var engine = new WorkflowEngine { McpClientFactory = new Transport() };
        engine.WithCopilotRunners(new Dictionary<string, string> { ["review"] = "code", ["edit"] = "code", ["inspect"] = "business" },
            new Dictionary<string, IReadOnlyList<CopilotToolBinding>> { ["inspect"] = [new("documents", "read")] });
        Assert.IsType<CopilotAgentTaskRunner>(engine.AgentTaskRunners["edit"]);
        Assert.IsType<CopilotAgentTaskRunner>(engine.AgentTaskRunners["review"]);
        Assert.IsType<BusinessAgentTaskRunner>(engine.AgentTaskRunners["inspect"]);
        Assert.Throws<ArgumentException>(() => engine.WithCopilotRunners([new("empty", "business")],
            new Dictionary<string, IReadOnlyList<CopilotToolBinding>> { ["empty"] = [] }));
        Assert.Throws<ArgumentException>(() => new BusinessAgentTaskRunner(new Transport(), [new("GnOuGo.Cmd.Mcp", "run")]));
        Assert.Throws<ArgumentException>(() => new BusinessAgentTaskRunner(new Transport(), [new("gnougo.githubcopilot.mcp", "code_agent_edit")]));
    }

    [Theory]
    [InlineData("success", "completed", 1)]
    [InlineData("paged_inputs", "completed", 1)]
    [InlineData("retry", "completed", 1)]
    [InlineData("late_retry", "budget_exhausted", 0)]
    [InlineData("fabricate", "completed", 0)]
    [InlineData("repeat", "failed", 1)]
    [InlineData("unknown", "failed", 0)]
    [InlineData("invalid_args", "failed", 0)]
    [InlineData("partial", "failed", 0)]
    [InlineData("error", "failed", 1)]
    [InlineData("interrupted", "needs_reconciliation", 1)]
    [InlineData("limit", "budget_exhausted", 1)]
    public async Task BusinessDecisionsUsePinnedContractsAndObservedReceipts(string test, string status, int actions)
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new Transport { Outcome = test };
        var runner = new BusinessAgentTaskRunner(transport, [new("documents", "read")]);
        var contract = await runner.DescribeAsync(ct);
        var id = contract.InputSchema["properties"]!["capabilities"]!["items"]!["enum"]![0]!.GetValue<string>();
        var model = new Model(test, id);
        var engine = new WorkflowEngine { McpClientFactory = transport, LLMClient = model, LlmDefaults = new() { Model = "fixture" } };
        var context = new AgentTaskContext("tenant", "run", "invocation", new()
        {
            Runner = "inspect", Objective = "Choose a record after observing the page", Workspace = "approved",
            Inputs = test == "paged_inputs" ? new() { ["data"] = new string('x', 5000) + "exact-tail-marker" } : new(),
            Capabilities = [id], Budget = new() { MaxActions = 1, MaxModelCalls = test == "limit" ? 1 : 4 },
            Verification = [new("record", "mcp.result", id, new() { ["type"] = "object" })],
            OutputSchema = new() { ["type"] = "object" }
        }) { Execution = new() { Engine = engine } };
        Assert.Empty(await runner.ValidateAsync(context, ct));
        Assert.NotEmpty(await runner.ValidateAsync(context with { Task = context.Task with { Capabilities = ["project.read"] } }, ct));
        var result = await runner.RunAsync(context, ct);
        Assert.Equal(status, result.Status);
        Assert.Equal(actions, transport.Calls);
        Assert.Equal(actions, result.Usage.Actions);
        Assert.Equal(model.Calls, result.Usage.ModelCalls);
        if (result.Status == "completed")
            Assert.Equal(test is "success" or "retry" or "paged_inputs", Assert.Single(await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, ct)).Passed);
        if (test == "paged_inputs")
        {
            Assert.Equal(3, model.Calls);
            Assert.Equal("mcp.result", Assert.Single(result.Evidence).Kind);
        }
        if (test == "success")
        {
            Assert.Equal(Transport.LargeContent, Assert.Single(result.Evidence).Facts["result"]!.GetValue<string>());
            Assert.Equal(2, model.Calls);
            Assert.Contains("next_offset", model.LastPrompt);
            Assert.DoesNotContain(Transport.LargeContent, model.LastPrompt);
        }
        var recovered = await runner.ReconcileAsync(context with { LastObservation = result }, ct);
        Assert.Same(result, recovered);
        Assert.Equal(actions, transport.Calls);
    }

    private sealed class Model(string scenario, string tool) : ILLMClient
    {
        public int Calls { get; private set; }
        public string? LastPrompt { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            LastPrompt = request.Prompt;
            Assert.True(request.DisableTransportRetries);
            if (scenario is "retry" or "late_retry" && Calls == 1)
                throw new LLMClientException(LLMClientFailureKind.RateLimited, "Rate limited", true, 429, null, 1, false,
                    scenario == "late_retry" ? 300000 : 1);
            if (scenario == "paged_inputs" && Calls == 1)
            {
                Assert.Contains("task-inputs", request.Prompt);
                Assert.DoesNotContain("exact-tail-marker", request.Prompt);
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["action"] = "read_page",
                    ["payload"] = """{"id":"task-inputs","offset":4096}""" },
                    Usage = new JsonObject { ["prompt_tokens"] = 70, ["completion_tokens"] = 10, ["total_tokens"] = 80 } });
            }
            if (scenario == "paged_inputs" && Calls == 2)
                Assert.Contains("exact-tail-marker", request.Prompt);
            var finish = scenario == "fabricate" || Calls > (scenario is "retry" or "paged_inputs" ? 2 : 1) && scenario != "repeat";
            return Task.FromResult(new LLMResponse
            {
                Json = new JsonObject { ["action"] = finish ? "finish" : scenario == "unknown" ? "shell" : tool,
                    ["payload"] = finish || scenario == "invalid_args" ? "{}" : """{"page":1}""" },
                CompletionStatus = scenario == "partial" ? "output_limit" : null,
                Usage = new JsonObject { ["prompt_tokens"] = 70, ["completion_tokens"] = 10, ["total_tokens"] = 80 }
            });
        }
    }

    private sealed class Transport : IMcpClientFactory, IMcpSession
    {
        internal static readonly string LargeContent = new('a', 10000);
        public int Calls { get; private set; }
        public string? Outcome { get; init; }
        public string ServerName => "documents";
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [];
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
        {
            Assert.Equal(ServerName, serverName);
            return Task.FromResult<IMcpSession>(this);
        }
        public Task<McpCallResult> CallToolAsync(string name, JsonNode? args, CancellationToken ct)
        {
            Assert.Equal("read", name);
            Calls++;
            if (Outcome == "interrupted") throw new IOException("lost receipt");
            return Task.FromResult(new McpCallResult { IsError = Outcome == "error", Content = JsonValue.Create(LargeContent) });
        }
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>(
            [new() { Name = "read", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{"page":{"type":"integer"}},"required":["page"],"additionalProperties":false}""") }]);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
