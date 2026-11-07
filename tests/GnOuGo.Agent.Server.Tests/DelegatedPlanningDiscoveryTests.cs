using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed class DelegatedPlanningDiscoveryTests
{
    [Theory]
    [InlineData("amazon", "GnOuGo.Browser.Mcp", "browser_get_content", "browser_fill")]
    [InlineData("code", "GnOuGo.Git.Mcp", "git_diff", "git_push")]
    public async Task PlanningOffersOnlyExistingDelegatedToolsAndPreservesTheirOriginalContracts(string scenario, string server, string allowed, string denied)
    {
        var inner = new Factory(server, allowed, denied);
        await using var discovery = new ServerLiveDiagnostics.DelegatedPlanningDiscovery(inner, scenario, "frozen-authority-assembly");
        await using var session = await discovery.GetClientAsync(server, TestContext.Current.CancellationToken);
        var tools = await session.ListToolsAsync(TestContext.Current.CancellationToken);
        Assert.Same(inner.Tools[0], Assert.Single(tools));
        Assert.True(DelegatedTestReviewAuthority.AllowsTool(scenario, server, tools[0].Name));
        Assert.Empty(await session.ListPromptsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CallToolAsync(allowed, new JsonObject(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CallToolAsync(denied, new JsonObject(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetPromptAsync("unadvertised", null, TestContext.Current.CancellationToken));
        Assert.Equal(0, inner.BusinessCalls);
    }

    [Fact]
    public async Task ConfigurationAndDelegatedPolicyBothBindSnapshotOwnershipAndTransportIsDisposed()
    {
        var inner = new Factory("GnOuGo.Browser.Mcp", "browser_get_content", "browser_fill");
        var discovery = new ServerLiveDiagnostics.DelegatedPlanningDiscovery(inner, "amazon", "policy-a");
        var original = discovery.SourceConfigurationFingerprint("GnOuGo.Browser.Mcp");
        Assert.NotEqual(original, new ServerLiveDiagnostics.DelegatedPlanningDiscovery(inner, "amazon", "policy-b").SourceConfigurationFingerprint("GnOuGo.Browser.Mcp"));
        Assert.NotEqual(original, new ServerLiveDiagnostics.DelegatedPlanningDiscovery(inner, "code", "policy-a").SourceConfigurationFingerprint("GnOuGo.Browser.Mcp"));
        inner.Configuration = "changed-config";
        Assert.NotEqual(original, discovery.SourceConfigurationFingerprint("GnOuGo.Browser.Mcp"));
        await discovery.DisposeAsync(); Assert.True(inner.Disposed);
    }

    [Fact]
    public async Task HostNameLookupRemainsAvailableWithoutAdvertisingAgentOperationsOrPermittingWrites()
    {
        var inner = new Factory("GnOuGo.Agent.Mcp", "agent_get_by_name", "agent_create");
        await using var discovery = new ServerLiveDiagnostics.DelegatedPlanningDiscovery(inner, "amazon", "frozen-authority-assembly");
        Assert.Empty(discovery.ServerMetadata);
        await using var session = await discovery.GetClientAsync("GnOuGo.Agent.Mcp", TestContext.Current.CancellationToken);
        Assert.Empty(await session.ListToolsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await session.ListResourcesAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await session.ListPromptsAsync(TestContext.Current.CancellationToken));
        await session.CallToolAsync("agent_get_by_name", new JsonObject { ["name"] = "independent-run" }, TestContext.Current.CancellationToken);
        Assert.Equal(1, inner.BusinessCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CallToolAsync("agent_create", new JsonObject { ["name"] = "independent-run" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetPromptAsync("unadvertised", null, TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.BusinessCalls);
    }

    [Fact]
    public async Task HostLookupRejectsMissingNamesAndAdditionalArguments()
    {
        var inner = new Factory("GnOuGo.Agent.Mcp", "agent_get_by_name", "agent_create");
        await using var discovery = new ServerLiveDiagnostics.DelegatedPlanningDiscovery(inner, "amazon", "frozen-authority-assembly");
        await using var session = await discovery.GetClientAsync("GnOuGo.Agent.Mcp", TestContext.Current.CancellationToken);
        foreach (var arguments in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["name"] = " " },
            new JsonObject { ["name"] = "run", ["unexpected"] = true } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CallToolAsync("agent_get_by_name", arguments, TestContext.Current.CancellationToken));
        Assert.Equal(0, inner.BusinessCalls);
    }

    private sealed class Factory(string server, string allowed, string denied) : IMcpClientFactory, IMcpSourceConfigurationFingerprint, IAsyncDisposable
    {
        internal readonly McpToolInfo[] Tools = [new() { Name = allowed, InputSchema = new JsonObject { ["type"] = "object" } }, new() { Name = denied }];
        internal int BusinessCalls;
        internal bool Disposed;
        internal string Configuration = "config";
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = server }];
        public string SourceConfigurationFingerprint(string serverName) => Configuration;
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct) => Task.FromResult<IMcpSession>(new Session(this, server));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private sealed class Session(Factory owner, string server) : IMcpSession
        {
            public string ServerName => server;
            public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>>(owner.Tools);
            public Task<IReadOnlyList<McpResourceInfo>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceInfo>>([]);
            public Task<IReadOnlyList<McpPromptInfo>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptInfo>>([new() { Name = "unadvertised" }]);
            public Task<McpGetPromptResult> GetPromptAsync(string promptName, JsonNode? arguments, CancellationToken ct) => throw new NotSupportedException();
            public Task<McpCallResult> CallToolAsync(string toolName, JsonNode? arguments, CancellationToken ct) { owner.BusinessCalls++; return Task.FromResult(new McpCallResult()); }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
