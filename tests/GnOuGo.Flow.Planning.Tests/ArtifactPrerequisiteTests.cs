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
    public async Task MissingProducerStopsBeforeAnyBindingRepair()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        plan.Root.Tasks.RemoveAt(0);
        plan.Root.Tasks[0].Inputs[0] = new("location", new() { Kind = "string", Text = "looks-like-a-resource" });
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        var runtime = new TestRuntime(); runtime.Proposal.Plan = plan;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        var error = Assert.Single(state.Diagnostics);
        Assert.Equal("TASK_ARTIFACT_PREREQUISITE_MISSING", error.Code);
        Assert.Equal("/tasks/use/inputs/location", error.Location);
        Assert.Contains("explicit semantic revision", error.Message);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.Yaml); Assert.Null(state.PendingCall);
        var again = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Same(state, again); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task CompactIndexPreservesDeclaredArtifactBusinessPorts()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = catalog.Capabilities.Where(c => c.Kind == "tool").Select(c => new McpToolInfo
        {
            Name = c.Method!, InputSchema = c.InputSchema, OutputSchema = c.OutputSchema, EffectKind = "read",
            ArtifactContract = new(c.ArtifactContract, [])
        }).ToList() });
        var discovery = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        state.Discovery.Sources = (await discovery.ListSourcesAsync(Ct)).ToList();
        state.Discovery.Pages.Add(await discovery.ListAsync(state.Discovery.Sources[0].Id, null, Ct, "unrelated query"));
        var coverage = PlanningDiscoveryContext.Coverage(state, new HashSet<string>(), PlanningDiscoveryContext.Candidates(state));
        var index = coverage[0]!["index"]!.AsArray();
        Assert.Equal("handle", index.Single(i => i!["name"]!.ToString() == "allocate")!["artifacts"]!["produces"]![0]!["port"]!.ToString());
        Assert.Equal("resource", index.Single(i => i!["name"]!.ToString() == "inspect")!["artifacts"]!["consumes"]![0]!["kind"]!.ToString());
        Assert.DoesNotContain("pointer", coverage.ToJsonString());
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("ambiguous")]
    [InlineData("denied")]
    public async Task UnresolvedOperationsDoNotProveProducerAbsence(string uncertainty)
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        plan.Root.Tasks[1].Inputs = [new("location", new() { Kind = "string", Text = "fabricated" })];
        var producer = catalog.Capabilities.Single(c => c.Id == "allocate");
        if (uncertainty == "unknown") catalog.Capabilities.Remove(producer);
        if (uncertainty == "ambiguous") catalog.Capabilities.Add(producer);
        if (uncertainty == "denied") catalog.Policy.DeniedCapabilityIds.Add(producer.Id);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Null(compiled.Graph);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "TASK_ARTIFACT_BINDING");
        Assert.DoesNotContain(compiled.Diagnostics, d => d.Code == "TASK_ARTIFACT_PREREQUISITE_MISSING");
    }

    [Fact]
    public async Task ExistingProducerKeepsBindingRepairButSettingsCannotRestartAnImpossibleRepair()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        plan.Root.Tasks[1].Inputs = [new("location", new() { Kind = "string", Text = "fabricated" })];
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        var runtime = new TestRuntime(); runtime.Proposal.Plan = plan;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal("TASK_ARTIFACT_BINDING", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(new[] { "/tasks/use/inputs/location" }, state.RevisionScope);
        runtime.Proposal.Plan.Root.Tasks[1].Inputs = [new("location", TaskArtifactBindingTests.Reference("make", "handle"))];
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ReplanAttempts);
        PlanningArtifactApproval.Verify(state);

        var stopped = PlannerFixture.Clone(state); stopped.Status = PlanningStatus.Generating;
        stopped.Plan!.Root.Tasks.RemoveAt(0); stopped.Plan.Root.Tasks[0].Inputs = [new("location", new() { Kind = "string", Text = "fake" })];
        stopped.Diagnostics = [new("TASK_ARTIFACT_BINDING", "/tasks/use/inputs/location", "Historical wrong origin")];
        stopped.RevisionScope = ["/tasks/use/inputs/location"];
        var before = JsonSerializer.Serialize(stopped, PlanningJsonContext.Default.PlanningSession);
        var result = await planner.AdvanceAsync(stopped, new() { ExpectedRevision = stopped.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(stopped.ModelCalls, result.ModelCalls); Assert.Equal(stopped.ReplanAttempts, result.ReplanAttempts);
        Assert.Equal(before, JsonSerializer.Serialize(stopped, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(result.RevisionScope); Assert.Null(result.ApprovedHash); Assert.Null(result.Yaml);
    }

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
    public async Task MissingPrerequisitesSearchOnlyInspectedSourcesOnceAndSurviveRecovery()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("consumer", new() { Tools = [Tool("noise_consumer", consumes: "resource")] });
        factory.RegisterServer("provider", new() { Tools = Enumerable.Range(0, 1000).Select(i => Tool("noise_" + i)).Append(Tool("unrelated", produces: "resource")).ToList() });
        factory.RegisterServer("uninspected", new() { Tools = [Tool("never_read")] });
        var real = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var tracked = new Tracked(real);
        var state = PlannerFixture.Session(); state.Request.Prompt = "noise"; state.Request.Generation.MaxInputTokensPerRequest = 24000;
        var runtime = new TestRuntime { Capabilities = tracked };
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.Discovery.Sources = (await real.ListSourcesAsync(Ct)).ToList();
        foreach (var name in new[] { "consumer", "provider" })
            state.Discovery.Pages.Add(await real.ListAsync(CapabilityDiscovery.SourceId(name), null, Ct, "noise"));
        runtime.Respond = (_, _) => throw new IOException("After retained dispatch");
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, Ct);
        Assert.NotNull(state.PendingCall);
        Assert.Equal(2, tracked.Reads.Count); Assert.All(tracked.Reads, r => Assert.Equal("resource", r.Kind));
        Assert.DoesNotContain(tracked.Reads, r => r.Source == CapabilityDiscovery.SourceId("uninspected"));
        var filtered = state.Discovery.Pages.Where(p => p.ProducedArtifactKind == "resource").ToArray();
        Assert.Equal(2, filtered.Length); Assert.Single(filtered.SelectMany(p => p.Capabilities));
        Assert.Equal("unrelated", HybridWorkflowPlanner.Shortlist(state)[0].Name);
        Assert.Contains("resource", state.PendingCall.Request.Prompt);
        var original = state.PendingCall.Request;
        state = PlannerFixture.Clone(state); state.Status = PlanningStatus.Generating;
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Proposal);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(2, tracked.Reads.Count); Assert.Equal(original.Prompt, runtime.Calls[^1].Prompt);
        Assert.Equal(original.ClientRequestId, runtime.Calls[^1].ClientRequestId);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(original.Prompt, original.StructuredOutputSchema!.AsObject()) < 24000);
        Assert.DoesNotContain(state.Catalog!.Capabilities, c => c.Method == "unrelated"); // Presentation is not execution selection.
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

    [Fact]
    public async Task PolicyExclusionsAndCyclicMetadataDoNotAuthorizeOperationsOrRepeatSearches()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [Tool("consumer", consumes: "resource"), Tool("cycle", produces: "resource", consumes: "resource")] });
        var catalog = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var source = Assert.Single(await catalog.ListSourcesAsync(Ct)).Id;
        var page = await catalog.ListAsync(source, null, Ct, "consumer");
        var state = PlannerFixture.Session(); state.Catalog = new() { AllowedStepTypes = ["mcp.call"] };
        state.Discovery.Sources = [new(source, "Declared")]; state.Discovery.Pages = [page];
        var cycle = page.Capabilities.Single(c => c.Name == "cycle");
        state.Discovery.Pages.Add(await catalog.ListAsync(source, null, Ct, "consumer", "resource"));
        Assert.Equal(cycle.Id, PlanningDiscoveryContext.Candidates(state)[0].Id);
        state.Catalog.Policy.DeniedCapabilityIds.Add(cycle.Id);
        Assert.DoesNotContain(PlanningDiscoveryContext.Candidates(state), c => c.Id == cycle.Id);
        Assert.DoesNotContain(HybridWorkflowPlanner.Shortlist(state), c => c.Id == cycle.Id);
        // Exact search returns metadata, but policy filtering still excludes it.
        var filtered = await catalog.ListAsync(source, null, Ct, "consumer", "resource");
        Assert.Equal("resource", filtered.ProducedArtifactKind); Assert.DoesNotContain(PlanningDiscoveryContext.Candidates(state), c => c.Id == cycle.Id);
        var before = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var runtime = new TestRuntime { Capabilities = new Tracked(catalog), Respond = (_, _) => throw new IOException("No completion") };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Empty(((Tracked)runtime.Capabilities).Reads);
        Assert.DoesNotContain(state.Discovery.Resolved, c => c.Id == cycle.Id);
        Assert.Equal(2, state.Discovery.Pages.Count); Assert.NotEmpty(before);
    }

    [Fact]
    public async Task UnavailableFilteredSourceIsRetainedAndDoesNotBlockAnIndependentPlan()
    {
        var (plan, catalog) = await TaskArtifactBindingTests.Fixture();
        var consumer = catalog.Capabilities.Single(c => c.Id == "inspect");
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        state.Discovery.Sources = [new("offline", "Inspected source")];
        state.Discovery.Pages = [new("offline", null, [new(consumer.Id, "offline", "consumer", "", "mcp.call", "read", consumer.Version,
            Operation: TaskOperations.Describe(consumer), ArtifactContract: consumer.ArtifactContract)], null)];
        var runtime = new TestRuntime { Capabilities = new Tracked(new Exact(consumer)) };
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, result.Status);
        var failure = Assert.Single(result.Discovery.Pages, p => p.ProducedArtifactKind is not null);
        Assert.Equal("resource", failure.ProducedArtifactKind); Assert.NotNull(failure.UnavailableReason);
        Assert.Contains(result.Discovery.Limitations, l => l.Contains("unavailable", StringComparison.Ordinal));
        Assert.Single(runtime.Calls); Assert.Single(((Tracked)runtime.Capabilities).Reads);
    }

    [Fact]
    public async Task FilteredContinuationUsesTheRetainedKindAndCannotBeChangedByTheModel()
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new() { Tools = Enumerable.Range(0, 10).Select(i => Tool("unrelated_" + i, produces: "resource"))
            .Append(Tool("wanted", consumes: "resource")).ToList() });
        var real = new CapabilityDiscovery(new() { McpClientFactory = factory }); var tracked = new Tracked(real);
        var source = Assert.Single(await real.ListSourcesAsync(Ct)).Id;
        var full = await real.ListAsync(source, null, Ct);
        var consumer = full.Capabilities.Single(c => c.Name == "wanted");
        var state = PlannerFixture.Session(); state.Request.Generation.MaxInputTokensPerRequest = 24000;
        var runtime = new TestRuntime { Capabilities = tracked };
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.Discovery.Sources = [new(source, "Declared")];
        state.Discovery.Pages = [new(source, null, [consumer], null)];
        var schema = PlanningSchemas.Proposal(state);
        var wire = JsonSerializer.SerializeToNode(new PlanningProposal { Requirements = PlannerFixture.Requirements(),
            DiscoveryRequests = [new(source, ProducedArtifactKind: "resource")] }, PlanningJsonContext.Default.PlanningProposal)!;
        wire = GnOuGo.Planning.Examples.PlanningCorpus.Transport(wire, schema, schema)!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, schema));
        wire["discoveryRequests"]![0]!["producedArtifactKind"] = "invented";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(wire, schema));
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Calls.Count == 1
            ? new() { Requirements = PlannerFixture.Requirements(), DiscoveryRequests = [new(source, runtime.Checkpoints[^1].Discovery.Pages.Single(p => p.ProducedArtifactKind == "resource").NextCursor)] }
            : runtime.Proposal);
        var result = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, result.Status); Assert.Equal(2, result.ModelCalls); Assert.Equal(0, result.ReplanAttempts);
        var pages = result.Discovery.Pages.Where(p => p.ProducedArtifactKind == "resource").ToArray();
        Assert.Equal(2, pages.Length); Assert.Equal(pages[0].NextCursor, pages[1].Cursor);
        Assert.Equal(10, pages.SelectMany(p => p.Capabilities).Select(c => c.Id).Distinct().Count());
        Assert.Equal(2, tracked.Reads.Count); Assert.All(tracked.Reads, r => Assert.Equal("resource", r.Kind));
    }

    private sealed class Exact(PlanningCapability capability) : ICapabilityCatalog
    {
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => throw new NotSupportedException();
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(capability);
    }

    private sealed class Tracked(ICapabilityCatalog inner) : ICapabilityCatalog
    {
        internal List<(string Source, string? Kind)> Reads = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => inner.ListSourcesAsync(ct);
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null)
        { Reads.Add((sourceId, producedArtifactKind)); return inner.ListAsync(sourceId, cursor, ct, query, producedArtifactKind); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => inner.ResolveAsync(summary, ct);
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
