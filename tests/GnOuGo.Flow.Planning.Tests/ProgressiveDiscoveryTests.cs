using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class ProgressiveDiscoveryTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoleSourceNeedsNoModelSelection_AndCannotBlockLocalPlanning(bool unavailable)
    {
        var catalog = new SingleSource(unavailable);
        var runtime = new TestRuntime { Capabilities = catalog };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls);
        Assert.Equal(1, catalog.Pages);
        Assert.DoesNotContain(state.Catalog!.Capabilities, c => c.Kind != "registered");
        Assert.Single(state.Discovery.Pages);
        Assert.NotEmpty(state.Discovery.Limitations);
    }

    private sealed class SingleSource(bool unavailable) : ICapabilityCatalog
    {
        public int Pages { get; private set; }
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CapabilitySource>>([new("source", "A declared source")]);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct)
        {
            Pages++;
            Assert.Null(cursor);
            if (unavailable) throw new IOException("Unavailable discovery transport");
            return Task.FromResult(new CapabilityPage(sourceId, null,
                [new("capability", sourceId, "operation", "An optional operation", "mcp.call", "read", "v1")], "next"));
        }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) =>
            throw new InvalidOperationException("Unselected contracts must not be fetched.");
    }

    [Fact]
    public async Task UnavailableUnrelatedSourcesDoNotPreventLocalPlanning()
    {
        var factory = new TrackingFactory(); var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Empty(factory.Contacts);
        Assert.Equal(2, state.Discovery.Sources.Count); Assert.DoesNotContain(state.Catalog!.Capabilities, c => c.Kind != "registered");
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
        runtime.Proposal.Plan = null; runtime.Proposal.SourceId = unavailable.Id;
        var state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Session(), new(), runtime, Ct);
        Assert.Single(state.Discovery.Limitations); Assert.DoesNotContain(state.Catalog!.Capabilities, c => c.Kind != "registered");
        runtime.Proposal.SourceId = null; runtime.Proposal.Plan = GnOuGo.Planning.Examples.PlanningCorpus.Greeting();
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Contains(state.Discovery.Limitations, l => l.Contains("unavailable", StringComparison.Ordinal)); Assert.Equal(1, runtime.Discoveries);
    }
    [Fact]
    public async Task EarlierPageSummariesRemainAvailableWithoutAReopenModelCall()
    {
        var factory = new TrackingFactory(); var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var source = (await runtime.Capabilities.ListSourcesAsync(Ct)).Single(s => s.Description == "Available source");
        runtime.Proposal.Plan = null; runtime.Proposal.SourceId = source.Id;
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, Ct);
        runtime.Proposal.Cursor = Assert.Single(state.Discovery.Pages).NextCursor;
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        runtime.Proposal.SourceId = null; runtime.Proposal.Cursor = null; runtime.Proposal.Plan = GnOuGo.Planning.Examples.PlanningCorpus.Greeting();
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, state.Discovery.Pages.Count);
        Assert.All(state.Discovery.Pages.SelectMany(p => p.Capabilities), c => Assert.Contains(c.Description, runtime.Calls[^1].Prompt));
        Assert.Single(factory.Contacts);
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
    [Fact]
    public async Task RunnerContractsAreRevalidatedAtTheirOwnSourceBeforeApproval()
    {
        var engine = new WorkflowEngine(); var runner = new Runner(); engine.AgentTaskRunners["coding"] = runner;
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new(), Ct);
        var source = Assert.Single(await runtime.Capabilities.ListSourcesAsync(Ct));
        var page = await runtime.Capabilities.ListAsync(source.Id, null, Ct);
        catalog.Capabilities.Add(await runtime.Capabilities.ResolveAsync(Assert.Single(page.Capabilities), Ct));
        Assert.Empty(await runtime.ValidateCatalogAsync(catalog, Ct));
        runner.Detail = "Changed approved contract";
        Assert.Contains(await runtime.ValidateCatalogAsync(catalog, Ct), f => f.Code == "CATALOG_CHANGED");
    }
    private sealed class Runner : IAgentTaskRunner
    {
        internal string Detail = "Declared runner contract";
        public Task<AgentTaskRunnerContract> DescribeAsync(CancellationToken ct) => Task.FromResult(new AgentTaskRunnerContract(Detail, AgentTaskContracts.InputSchema));
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext c, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AgentTaskResult> RunAsync(AgentTaskContext c, CancellationToken ct) => throw new NotSupportedException();
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext c, CancellationToken ct) => throw new NotSupportedException();
    }
    [Fact]
    public async Task TaskRepairReusesResolvedOperationReceiptsAndKeepsIndependentTasks()
    {
        var source = new RepairCatalog(); var runtime = new TestRuntime { Capabilities = source };
        runtime.Proposal.Plan!.Root.Tasks.Add(new() { Id = "work", Objective = "Read the requested value", Operation = "selected",
            Inputs = [new("value", GnOuGo.Planning.Examples.PlanningCorpus.Number(1))] });
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_INPUT_TYPE"); Assert.Equal(["work"], state.RevisionScope);
        var receipt = System.Text.Json.JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability);
        runtime.Proposal.Plan.Root.Tasks[1].Inputs[0].Value.Kind = "string"; runtime.Proposal.Plan.Root.Tasks[1].Inputs[0].Value.Text = "business value";
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(1, source.Resolutions); Assert.Single(state.Discovery.Pages);
        Assert.Equal(receipt, System.Text.Json.JsonSerializer.Serialize(state.Discovery.Resolved[0], PlanningJsonContext.Default.PlanningCapability));
    }
    private sealed class RepairCatalog : ICapabilityCatalog
    {
        public int Resolutions;
        private static PlanningCapability Capability() => new() { Id = "selected", Version = "v1", StepType = "mcp.call", Server = "source", Method = "read", Kind = "tool", EffectKind = "read",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject() };
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>([new("source", "Declared source")]);
        public Task<CapabilityPage> ListAsync(string id, string? cursor, CancellationToken ct) => Task.FromResult(new CapabilityPage(id, cursor,
            [new("selected", id, "read", "Read a declared value", "mcp.call", "read", "v1", Operation: TaskOperations.Describe(Capability()))], null));
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) { Resolutions++; return Task.FromResult(Capability()); }
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
