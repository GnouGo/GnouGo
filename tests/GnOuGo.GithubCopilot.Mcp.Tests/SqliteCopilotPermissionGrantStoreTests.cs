using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class SqliteCopilotPermissionGrantStoreTests
{
    [Fact]
    public async Task FutureAgentGrant_SurvivesStoreRestartAndFollowsAgentRename()
    {
        using var database = new TemporaryPermissionDatabase();
        var first = database.CreateStore();
        var original = Context("tenant-a", "execution-a", "agent-a", "Old name");

        var created = await first.GrantFutureAgentRunsAsync(original, TestContext.Current.CancellationToken);
        var restarted = database.CreateStore();
        var renamed = Context("tenant-a", "execution-b", "agent-a", "New name");
        var loaded = await restarted.FindReusableGrantAsync(renamed, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded!.Id);
        Assert.Equal(CopilotPermissionGrantScope.FutureAgentRuns, loaded.Scope);

        var updated = await restarted.GrantFutureAgentRunsAsync(renamed, TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("New name", updated.AgentName);
    }

    [Fact]
    public async Task FutureAgentGrant_IsIsolatedByTenantAndAgentAndCanBeRevoked()
    {
        using var database = new TemporaryPermissionDatabase();
        var store = database.CreateStore();
        var grant = await store.GrantFutureAgentRunsAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken);

        Assert.Null(await store.FindReusableGrantAsync(Context("tenant-b", "execution-a", "agent-a", "Reviewer"), TestContext.Current.CancellationToken));
        Assert.Null(await store.FindReusableGrantAsync(Context("tenant-a", "execution-a", "agent-b", "Reviewer"), TestContext.Current.CancellationToken));
        Assert.True(await store.RevokeAsync("tenant-a", grant.Id, TestContext.Current.CancellationToken));
        Assert.Null(await store.FindReusableGrantAsync(Context("tenant-a", "execution-a", "agent-a", "Reviewer"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkflowGrant_IsSharedOnlyWithinStableExecutionIdentity()
    {
        using var database = new TemporaryPermissionDatabase();
        var store = database.CreateStore();
        await store.GrantWorkflowRunAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken);

        var sameExecution = await store.FindReusableGrantAsync(
            Context("tenant-a", "execution-a", "agent-b", "Other"),
            TestContext.Current.CancellationToken);
        var otherExecution = await store.FindReusableGrantAsync(
            Context("tenant-a", "execution-b", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken);
        var otherTenant = await store.FindReusableGrantAsync(
            Context("tenant-b", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CopilotPermissionGrantScope.WorkflowRun, sameExecution?.Scope);
        Assert.Null(otherExecution);
        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task ConcurrentPersistentWrites_ProduceOneTenantAgentGrant()
    {
        using var database = new TemporaryPermissionDatabase();
        var stores = Enumerable.Range(0, 8).Select(_ => database.CreateStore()).ToArray();
        await Task.WhenAll(stores.Select((store, index) => store.GrantFutureAgentRunsAsync(
            Context("tenant-a", $"execution-{index}", "agent-a", $"Reviewer {index}"),
            TestContext.Current.CancellationToken)));

        var grants = await stores[0].ListFutureAgentGrantsAsync("tenant-a", TestContext.Current.CancellationToken);
        Assert.Single(grants);
        Assert.Equal("agent-a", grants[0].AgentId);
    }

    private static CopilotRequestContext Context(string tenant, string execution, string agent, string name)
        => new(tenant, ExecutionId: execution, AgentId: agent, AgentName: name);

    private sealed class TemporaryPermissionDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gnougo-copilot-permissions-tests", Guid.NewGuid().ToString("N"));

        public SqliteCopilotPermissionGrantStore CreateStore()
            => new(Options.Create(new CodeServerSettings
            {
                Copilot = new CodeCopilotSettings
                {
                    PermissionDatabasePath = Path.Combine(_directory, "permissions.db"),
                    WorkflowGrantTtlSeconds = 3600
                }
            }));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
