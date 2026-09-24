using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class ProgressiveDiscoveryTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;
    [Fact]
    public async Task UnavailableUnrelatedSourcesDoNotPreventLocalPlanning()
    {
        var factory = new TrackingFactory(); var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Empty(factory.Contacts);
        Assert.Equal(2, state.Discovery.Sources.Count); Assert.Empty(state.Catalog!.Capabilities);
    }
    [Fact]
    public async Task PagesAreCachedAndOnlySelectedContractsAreResolved()
    {
        var factory = new TrackingFactory(); var discovery = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var sources = await discovery.ListSourcesAsync(Ct); Assert.Empty(factory.Contacts);
        var selected = sources.Single(s => s.Description == "Available source");
        var page = await discovery.ListAsync(selected.Id, null, Ct);
        Assert.Equal(24, page.Capabilities.Count); Assert.NotNull(page.NextCursor);
        var again = await discovery.ListAsync(selected.Id, null, Ct);
        var second = await discovery.ListAsync(selected.Id, page.NextCursor, Ct);
        var exact = await discovery.ResolveAsync(page.Capabilities[0], Ct);
        Assert.Equal(page.Capabilities[0].Id, exact.Id); Assert.Equal(page.Capabilities[0].Version, exact.Version);
        Assert.Equal(page.Capabilities.Select(c => c.Id), again.Capabilities.Select(c => c.Id)); Assert.Single(second.Capabilities); Assert.Equal(new[] { "available" }, factory.Contacts);
        await Assert.ThrowsAsync<PlanningConflictException>(() => discovery.ResolveAsync(page.Capabilities[0] with { Version = "changed" }, Ct));
    }
    [Fact]
    public async Task FailedDiscoveryIsRetainedAndDoesNotAbortAValidGraph()
    {
        var runtime = new TestRuntime(new() { McpClientFactory = new TrackingFactory() });
        var unavailable = (await runtime.Capabilities.ListSourcesAsync(Ct)).Single(s => s.Description == "Unavailable source");
        runtime.Proposal.Graph = null; runtime.Proposal.SourceId = unavailable.Id;
        var state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Session(), new(), runtime, Ct);
        Assert.Single(state.Discovery.Limitations); Assert.Empty(state.Catalog!.Capabilities);
        runtime.Proposal.SourceId = null; runtime.Proposal.Graph = PlannerFixture.Greeting();
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Contains(state.Discovery.Limitations, l => l.Contains("unavailable", StringComparison.Ordinal)); Assert.Equal(1, runtime.Discoveries);
    }
    [Fact]
    public async Task SelectedContractChangesAreDetectedWithoutConnectingOtherSources()
    {
        var factory = new TrackingFactory(); var engine = new WorkflowEngine { McpClientFactory = factory };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), Ct);
        var source = (await runtime.Capabilities.ListSourcesAsync(Ct)).Single(s => s.Description == "Available source");
        var page = await runtime.Capabilities.ListAsync(source.Id, null, Ct);
        var selected = await runtime.Capabilities.ResolveAsync(page.Capabilities[0], Ct); catalog.Capabilities.Add(selected);
        Assert.Empty(await runtime.ValidateCatalogAsync(catalog, Ct));
        factory.Configuration.Tools.Single(t => t.Name == selected.Method).OutputSchema = new JsonObject { ["type"] = "string" };
        Assert.NotEmpty(await runtime.ValidateCatalogAsync(catalog, Ct));
        Assert.DoesNotContain("unavailable", factory.Contacts);
    }
    private sealed class TrackingFactory : IMcpClientFactory
    {
        internal List<string> Contacts { get; } = [];
        internal MockMcpServerConfig Configuration { get; } = new() { Tools = Enumerable.Range(0, 25).Select(i => new McpToolInfo
        {
            Name = "operation_" + i, Description = "Operation " + i, EffectKind = "read",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
            OutputSchema = new JsonObject { ["type"] = "number" }
        }).ToList() };
        public IReadOnlyList<McpServerMetadata> ServerMetadata => [new() { Name = "available", Description = "Available source" }, new() { Name = "unavailable", Description = "Unavailable source" }];
        public Task<IMcpSession> GetClientAsync(string serverName, CancellationToken ct)
        {
            Contacts.Add(serverName);
            if (serverName == "unavailable") throw new IOException("Unavailable");
            var factory = new InMemoryMcpClientFactory(); factory.RegisterServer(serverName, Configuration); return factory.GetClientAsync(serverName, ct);
        }
    }
}
