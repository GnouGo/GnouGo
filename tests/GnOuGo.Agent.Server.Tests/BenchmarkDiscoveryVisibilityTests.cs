using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Copilot;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkDiscoveryVisibilityTests
{
    [Theory]
    [InlineData("amazon", "GnOuGo.Browser.Mcp", "browser_get_content", "browser_click")]
    [InlineData("amazon", "GnOuGo.Document.Mcp", "document_write", "document_delete")]
    [InlineData("code", "GnOuGo.Git.Mcp", "git_clone", "git_push")]
    [InlineData("code", "GnOuGo.GithubCopilot.Mcp", "code_agent_edit", "copilot_interactive_one_shot")]
    [InlineData("code", "GnOuGo.Cmd.Mcp", "cmd_run", "cmd_list_allowed_commands")]
    [InlineData("code", "GnOuGo.Document.Mcp", "document_write", "document_delete")]
    public async Task IsolatedDiscoveryPreservesAllowedOriginalsAndCannotResolveUnsupportedMethods(
        string scenario, string server, string allowed, string denied)
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new OriginalSession(server, allowed, denied);
        var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, _ => Task.CompletedTask, discoveryScenario: scenario);
        await using var session = await observed.GetClientAsync(server, ct);
        Assert.Same(transport.Tools[0], Assert.Single(await session.ListToolsAsync(ct)));
        Assert.Empty(await session.ListPromptsAsync(ct));
        Assert.Same(transport.ServerMetadata, observed.ServerMetadata);

        var original = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = transport }, (_, _) => Task.CompletedTask);
        var filtered = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = observed }, (_, _) => Task.CompletedTask);
        var source = Assert.Single(await filtered.Capabilities.ListSourcesAsync(ct));
        Assert.Equal(Assert.Single(await original.Capabilities.ListSourcesAsync(ct)), source);
        var before = await original.Capabilities.ListAsync(source.Id, null, ct);
        var after = await filtered.Capabilities.ListAsync(source.Id, null, ct);
        var summary = Assert.Single(after.Capabilities);
        var originalAllowed = before.Capabilities.Single(c => c.Name == allowed && c.EffectKind == "execute");
        Assert.Equal(originalAllowed.Id, summary.Id);
        Assert.Equal(originalAllowed.Version, summary.Version);
        var originalContract = await original.Capabilities.ResolveAsync(originalAllowed, ct);
        var filteredContract = await filtered.Capabilities.ResolveAsync(summary, ct);
        Assert.Equal(JsonSerializer.Serialize(originalContract, PlanningJsonContext.Default.PlanningCapability),
            JsonSerializer.Serialize(filteredContract, PlanningJsonContext.Default.PlanningCapability));
        foreach (var unavailable in before.Capabilities.Where(c => c.Id != summary.Id))
            await Assert.ThrowsAsync<PlanningConflictException>(() => filtered.Capabilities.ResolveAsync(unavailable, ct));
        var catalog = await filtered.DiscoverAsync(new(), ct);
        Assert.Contains("set", catalog.AllowedStepTypes);
        Assert.DoesNotContain("agent.run", catalog.AllowedStepTypes);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task UnfilteredHostsRetainToolsPromptsAndExplicitAdaptiveRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new OriginalSession("GnOuGo.GithubCopilot.Mcp", "code_agent_edit", "copilot_interactive_one_shot");
        var observed = new LiveWorkflowEvaluation.ObservedMcp(transport, _ => Task.CompletedTask);
        await using var session = await observed.GetClientAsync(transport.ServerName, ct);
        Assert.Same(transport.Tools, await session.ListToolsAsync(ct));
        Assert.Same(transport.Prompts, await session.ListPromptsAsync(ct));
        var engine = new WorkflowEngine { McpClientFactory = transport }
            .WithCopilotRunners([new("coding", transport.ServerName)]);
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        Assert.Equal(2, (await runtime.Capabilities.ListSourcesAsync(ct)).Count);
        Assert.Contains("agent.run", (await runtime.DiscoverAsync(new(), ct)).AllowedStepTypes);
        Assert.Equal(0, transport.Calls);
    }

    private sealed class OriginalSession(string server, string allowed, string denied) : IMcpClientFactory, IMcpSession
    {
        public string ServerName => server;
        public IReadOnlyList<McpServerMetadata> ServerMetadata { get; } = [new() { Name = server, Description = "Original provider description" }];
        internal IReadOnlyList<McpToolInfo> Tools { get; } = [Tool(allowed), Tool(denied)];
        internal IReadOnlyList<McpPromptInfo> Prompts { get; } = [new() { Name = allowed, Description = "A prompt is not the same named tool" }];
        internal int Calls;
        private static McpToolInfo Tool(string name) => new() { Name = name, Description = "Original normative contract", EffectKind = "execute",
            InputSchema = JsonNode.Parse("""{"type":"object","required":["value"],"properties":{"value":{"type":"string","minLength":1}},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"complete":{"type":"boolean"}}}"""),
            Meta = JsonNode.Parse("""{"origin":"provider","constraints":"unchanged"}"""), ExampleResponse = new JsonObject { ["complete"] = true } };
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => Task.FromResult<IMcpSession>(this);
        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult(Tools);
        public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult(Prompts);
        public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
        public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct) { Calls++; throw new InvalidOperationException("Discovery cannot execute tools."); }
        public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct) { Calls++; throw new InvalidOperationException("Discovery cannot resolve prompts."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
