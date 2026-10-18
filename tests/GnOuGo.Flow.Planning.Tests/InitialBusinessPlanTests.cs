using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InitialBusinessPlanTests
{
    [Fact]
    public async Task FreshTransformsRequireModeWhileHistoricalSchemasRemainExecutable()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Tasks = [new() { Id = "greet", Kind = "transform", Mode = "interpret", Objective = "Choose a greeting",
            Inputs = [new("text", new() { Kind = "string", Text = "Hello" })],
            ResultType = new() { Kind = "object", Fields = [new() { Name = "message", Type = new() }] } }];
        var request = new PlanningPrompt(state).Request();
        var response = TestRuntime.Response(request, runtime.Proposal).Json!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        response["plan"]!["root"]!["tasks"]![0]!.AsObject().Remove("mode");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!));
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, PlanningSchemas.FullProposal(state)));
        runtime.Proposal.Plan.Root.Tasks[0].Mode = null;
        Assert.Empty(new TaskPlanCompiler().Compile(runtime.Proposal.Plan, state.Catalog).Diagnostics);
    }

    [Theory]
    [InlineData("{\"kind\":\"string\",\"text\":\"yes\"}", false)]
    [InlineData("{\"kind\":\"number\",\"number\":1}", false)]
    [InlineData("{\"kind\":\"index\"}", false)]
    [InlineData("{\"kind\":\"null\"}", false)]
    [InlineData("{\"kind\":\"boolean\",\"boolean\":false}", true)]
    [InlineData("{\"kind\":\"output\",\"source\":\"earlier\",\"port\":\"flag\"}", true)]
    [InlineData("{\"kind\":\"predicate\",\"predicate\":\"not_equal\",\"items\":[{\"kind\":\"input\",\"source\":\"value\"},{\"kind\":\"null\"}]}", true)]
    public void FreshAssertionsConstrainKnownResultsWithoutGuessingReferenceTypes(string binding, bool accepted)
    {
        var state = PlannerFixture.Session(); state.Catalog = new(); var request = new PlanningPrompt(state).Request();
        var response = TestRuntime.Response(request, new TestRuntime().Proposal).Json!;
        response["plan"]!["root"]!["tasks"]![0]!["requires"] = JsonNode.Parse(binding);
        Assert.Equal(accepted, PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!).Count == 0);
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, PlanningSchemas.FullProposal(state)));
    }

    [Fact]
    public async Task FailedOptionalResolutionIsReusedAcrossRecoveryButExplicitInspectionCanRetry()
    {
        var state = GlobalDiscoveryTests.State();
        var page = state.Discovery.Pages.Single(p => p.SourceId == "b");
        var summary = page.Capabilities[0];
        var exact = state.Discovery.Resolved.Single(c => c.Id == summary.Id);
        state.Discovery.Pages = [page with { Capabilities = [summary] }]; state.Discovery.Resolved.Clear();
        var catalog = new ResolutionCatalog(exact); var runtime = new TestRuntime { Capabilities = catalog };
        await PlanningDiscoveryContext.ResolveShortlistAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(1, catalog.Calls); Assert.Single(state.Discovery.Limitations);
        state = PlannerFixture.Clone(state);
        await PlanningDiscoveryContext.ResolveShortlistAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(1, catalog.Calls);
        Assert.Contains(summary.Id, new PlanningPrompt(state).Request().Prompt);
        state.Discovery.Inspections = [new("b", OperationIds: [summary.Operation!.Id])]; catalog.Available = true;
        await PlanningDiscoveryContext.ResolveShortlistAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(2, catalog.Calls); Assert.Single(state.Discovery.Resolved);
        Assert.Contains(PlanningDiscoveryContext.Required(state), o => o.Id == summary.Operation.Id);
    }

    private sealed class ResolutionCatalog(PlanningCapability exact) : ICapabilityCatalog
    {
        internal int Calls;
        internal bool Available;
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => throw new InvalidOperationException();
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        { Calls++; return Available ? Task.FromResult(exact) : throw new IOException("Private failure"); }
    }
}
