using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ArtifactPrerequisiteTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;

    [Fact]
    public async Task ExactFilterFindsFarTailProducersAndBindsContinuationToMetadataAndKind()
    {
        var factory = new InMemoryMcpClientFactory();
        var tools = Enumerable.Range(0, 1000).Select(i => Tool("noise_" + i)).ToList();
        tools.AddRange(Enumerable.Range(0, 10).Select(i => Tool("unrelated_" + i, produces: "resource")));
        tools.Add(Tool("other", produces: "different"));
        var config = new MockMcpServerConfig { Tools = tools };
        factory.RegisterServer("renamed", config);
        var discovery = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var source = Assert.Single(await discovery.ListSourcesAsync(Ct)).Id;
        var regular = await discovery.ListAsync(source, null, Ct, "noise");
        Assert.All(regular.Capabilities, c => Assert.StartsWith("noise_", c.Name));
        var page = await discovery.ListAsync(source, null, Ct, "noise", "resource");
        Assert.Equal(8, page.Capabilities.Count); Assert.Equal("resource", page.ProducedArtifactKind);
        Assert.All(page.Capabilities, c => Assert.Contains(c.ArtifactContract!.Produces, p => p.Kind == "resource"));
        var next = await discovery.ListAsync(source, page.NextCursor, Ct, "noise", "resource");
        Assert.Equal(2, next.Capabilities.Count); Assert.Null(next.NextCursor);
        Assert.Equal(10, page.Capabilities.Concat(next.Capabilities).Select(c => c.Id).Distinct().Count());
        await Assert.ThrowsAsync<ArgumentException>(() => discovery.ListAsync(source, page.NextCursor, Ct, "noise", "different"));
        await Assert.ThrowsAsync<ArgumentException>(() => discovery.ListAsync(source, page.NextCursor, Ct, "changed", "resource"));
        tools.Reverse(); var reordered = new CapabilityDiscovery(new() { McpClientFactory = factory });
        Assert.Equal(page.Capabilities.Select(c => c.Id), (await reordered.ListAsync(source, null, Ct, "noise", "resource")).Capabilities.Select(c => c.Id));
        tools[0].Description += " changed version";
        var changed = new CapabilityDiscovery(new() { McpClientFactory = factory });
        await Assert.ThrowsAsync<ArgumentException>(() => changed.ListAsync(source, page.NextCursor, Ct, "noise", "resource"));
        var empty = await discovery.ListAsync(source, null, Ct, "noise", "undeclared");
        Assert.Empty(empty.Capabilities); Assert.Null(empty.NextCursor);
    }

    [Fact]
    public async Task ExactContractMustRetainTheDiscoveredArtifactDeclaration()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        var producer = catalog.Capabilities.Single(c => c.Id == "allocate");
        var wrong = new CapabilitySummary(producer.Id, "s", "arbitrary", "", producer.StepType, producer.EffectKind, producer.Version,
            Operation: TaskOperations.Describe(producer), ArtifactContract: new(1, [new("forged", "/handle", "materialize")], []));
        var runtime = new TestRuntime { Capabilities = new Exact(producer) };
        await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningDiscoveryContext.ResolveAsync(state, runtime, wrong, Ct));
        Assert.Empty(state.Discovery.Resolved);
        state.Discovery.Resolved.Add(producer);
        await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningDiscoveryContext.ResolveAsync(state, runtime, wrong, Ct));
        Assert.Same(producer, Assert.Single(state.Discovery.Resolved));
    }

    [Fact]
    public async Task OptionalArtifactRemovalRemainsRepairableWithoutAProducer()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        plan.Root.Tasks.RemoveAt(0);
        var consumer = catalog.Capabilities.Single(c => c.Id == "inspect");
        consumer.InputSchema.Remove("required"); consumer.ArtifactContract = new(1, [], [new("resource", "/location", false)]);
        plan.Root.Tasks[0].Inputs = [new("location", new() { Kind = "string", Text = "fabricated" })];
        var result = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Equal("TASK_ARTIFACT_BINDING", Assert.Single(result.Diagnostics).Code);
        var scope = TaskPlanRevisions.Scope(plan, result.Diagnostics);
        var corrected = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        corrected.Root.Tasks[0].Inputs.Clear();
        Assert.Empty(TaskPlanRevisions.Validate(plan, corrected, scope, catalog));
        Assert.Empty(new TaskPlanCompiler().Compile(corrected, catalog).Diagnostics);
    }

    private sealed class Exact(PlanningCapability capability) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => throw new NotSupportedException();
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(capability);
    }

    private static McpToolInfo Tool(string name, string? produces = null, string? consumes = null) => new()
    {
        Name = name, Description = "Declared utility", EffectKind = "read",
        InputSchema = JsonNode.Parse("""{"type":"object","properties":{"resource":{"type":"string"}}}""")!,
        OutputSchema = JsonNode.Parse("""{"type":"object","required":["handle"],"properties":{"handle":{"type":"string"}}}""")!,
        ArtifactContract = new(new(1, produces is null ? [] : [new(produces, "/handle", "materialize")],
            consumes is null ? [] : [new(consumes, "/resource", true)]), [])
    };

}
