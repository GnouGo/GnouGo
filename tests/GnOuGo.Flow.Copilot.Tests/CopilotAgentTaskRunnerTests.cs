using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Copilot.Tests;

public sealed class CopilotAgentTaskRunnerTests
{
    [Fact]
    public async Task RecoveryInspectsOriginalIdentityWithoutDispatchingAgain()
    {
        var transport = new Transport(); var runner = new CopilotAgentTaskRunner(transport, "configured-server");
        var task = Context();
        Assert.Empty(await runner.ValidateAsync(task, TestContext.Current.CancellationToken));
        await runner.RunAsync(task, TestContext.Current.CancellationToken);
        await runner.ReconcileAsync(task, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "copilot_task_validate", "copilot_task_run", "copilot_task_inspect" }, transport.Calls.Select(c => c.Method));
        Assert.All(transport.Calls, call =>
        {
            var sent = JsonSerializer.Deserialize(call.Input!["contextJson"]!.GetValue<string>(), AgentTaskJsonContext.Default.AgentTaskContext)!;
            Assert.Equal(task.InvocationId, sent.InvocationId); Assert.Equal(task.TenantId, sent.TenantId); Assert.Equal(task.RunId, sent.RunId);
        });
    }
    [Fact]
    public async Task SelectedRunnerDeclaresExactScopeContractAndRejectsOldProtocol()
    {
        var transport = new Transport(); var runner = new CopilotAgentTaskRunner(transport, "configured-server");
        var contract = await runner.DescribeAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Declared scope", contract.Description);
        transport.Version = 8;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DescribeAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task FabricatedCompletionIsNotUpgradedToExecutionEvidence()
    {
        var runner = new CopilotAgentTaskRunner(new Transport(), "configured-server"); var context = Context();
        var result = await runner.RunAsync(context, TestContext.Current.CancellationToken);
        Assert.Empty(result.Evidence);
        var findings = await new EvidenceAgentTaskVerifier().VerifyAsync(context, result, TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(findings).Passed);
    }
    [Fact]
    public async Task TransportErrorsRemainErrorsAndDoNotTriggerAutomaticRetries()
    {
        var transport = new Transport { Fail = true }; var runner = new CopilotAgentTaskRunner(transport, "configured-server");
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Context(), TestContext.Current.CancellationToken));
        Assert.Single(transport.Calls);
    }
    private static AgentTaskContext Context() => new("tenant", "run", "/workflow/main/branch/2/iteration/3/step/code", new()
    {
        Runner = "coding", Objective = "Implement and test the change", Workspace = "/project", Capabilities = ["project.read", "project.write", "command.execute"],
        OutputSchema = new() { ["type"] = "object" }, Verification = [new("tests", "command.exit", "dotnet test", new() { ["type"] = "object" })]
    });
    private sealed class Transport : IMcpClientFactory, IMcpSession
    {
        public List<(string Method, JsonNode? Input)> Calls { get; } = [];
        public int Version { get; set; } = 9;
        public bool Fail { get; set; }
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [];
        public string ServerName => "configured-server";
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) { Assert.Equal(ServerName, serverName); return Task.FromResult<IMcpSession>(this); }
        public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct)
        {
            Calls.Add((toolName, arguments?.DeepClone()));
            JsonNode? body = toolName switch
            {
                "copilot_task_contract" => new JsonObject { ["schemaVersion"] = Version, ["contract"] = JsonSerializer.SerializeToNode(new AgentTaskRunnerContract("Declared scope", AgentTaskContracts.InputSchema), AgentTaskJsonContext.Default.AgentTaskRunnerContract) },
                "copilot_task_validate" => new JsonObject { ["errors"] = new JsonArray() },
                _ => JsonSerializer.SerializeToNode(new AgentTaskResult("completed", new JsonObject(), [], [], new(1, 100, 1), "Everything passed!"), AgentTaskJsonContext.Default.AgentTaskResult)
            };
            return Task.FromResult(new McpCallResult { IsError = Fail, Content = body });
        }
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([]);
        public Task<McpGetPromptResult> GetPromptAsync(string name, JsonNode? arguments, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
