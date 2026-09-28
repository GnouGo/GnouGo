using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class GlobalDiscoveryTests
{
    [Fact]
    public void ContinuationKeepsEarlierCandidatesEligibleWithoutRepeatingHistoricalPages()
    {
        var state = State();
        state.Discovery.Pages.Add(new("a", "next-a", [new("later", "a", "unrelated", "Later metadata", "mcp.call", "read", "v1",
            Operation: new() { Id = "later", Description = "unselected-full-contract" })], null, Query: "find needle records"));
        Assert.All(HybridWorkflowPlanner.Shortlist(state).Take(8), c => Assert.StartsWith("a", c.Id));
        var context = Context(HybridWorkflowPlanner.Prompt(state));
        var index = context["coverage"]!.AsArray().Single(c => c!["sourceId"]!.ToString() == "a")!["index"]!.AsArray();
        Assert.Equal("later", Assert.Single(index)!["id"]!.ToString());
        Assert.DoesNotContain(context["operations"]!.AsArray(), o => o!["id"]!.ToString() == "later");
        Assert.Equal(4, state.Discovery.Pages.Count);
    }

    [Fact]
    public void ConflictingVersionsAreNotEligibleForAutomaticShortlisting()
    {
        var state = State();
        var first = state.Discovery.Pages[0].Capabilities[0];
        state.Discovery.Pages.Add(new("a", "next-a", [first with { Version = "changed" }], null, Query: "find needle records"));
        Assert.DoesNotContain(HybridWorkflowPlanner.Shortlist(state), c => c.Id == first.Id);
    }

    [Fact]
    public async Task ConflictingVersionsCannotReuseAPreviouslyResolvedOperation()
    {
        var state = State(); var first = state.Discovery.Pages[0].Capabilities[0];
        state.Discovery.Pages.Add(new("a", "next-a", [first with { Version = "changed" }], null, Query: "find needle records"));
        var runtime = new TestRuntime();
        runtime.Proposal.Plan = new() { Root = new() { Tasks = [new() { Id = "use", Objective = "Use selected operation", Operation = first.Operation!.Id }] } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("SELECTED_OPERATION_UNAVAILABLE", Assert.Single(state.Diagnostics).Code);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.Yaml);
    }

    [Fact]
    public void TokenBudgetHasNoSourceQuotaAndRequiredContractsAppearExactlyOnce()
    {
        var state = State();
        var selected = HybridWorkflowPlanner.Shortlist(state);
        Assert.Equal(24, selected.Count);
        Assert.All(selected.Take(8), c => Assert.Equal("a", c.SourceId));
        var identities = selected.Select(c => c.Id).ToArray();
        state.Discovery.Sources.Reverse(); state.Discovery.Pages.Reverse();
        Assert.Equal(identities, HybridWorkflowPlanner.Shortlist(state).Select(c => c.Id));
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "use", Objective = "Use the selected operation", Operation = "a0" }] } };
        var prompt = HybridWorkflowPlanner.BuildPrompt(state, HybridWorkflowPlanner.Shortlist(state));
        var context = Context(prompt);
        Assert.Equal(24, context["operations"]!.AsArray().Count);
        Assert.Single(context["operations"]!.AsArray(), o => o!["id"]!.ToString() == "a0");
        Assert.Contains("schema-only-b7", prompt);
        state.Catalog!.Policy.DeniedCapabilityIds.Add("a1");
        Assert.DoesNotContain(HybridWorkflowPlanner.Shortlist(state), c => c.Id == "a1");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("field")]
    public async Task FarTailToolSurvivesGlobalRankingAcrossLargeSources(string match)
    {
        var factory = new InMemoryMcpClientFactory();
        for (var source = 0; source < 3; source++)
        {
            var tools = Enumerable.Range(0, 1000).Select(i => new McpToolInfo
            {
                Name = "utility_" + i, Description = "General utility", EffectKind = "read",
                InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
                OutputSchema = new JsonObject { ["type"] = "string" }
            }).ToList();
            if (source == 2)
            {
                if (match == "name") tools[^1].Name = "ReadRésuméEntries";
                if (match == "description") tools[^1].Description = "Read résumé entries";
                if (match == "field") tools[^1].InputSchema!["properties"]!["résuméEntries"] = new JsonObject { ["type"] = "string" };
            }
            factory.RegisterServer("arbitrary_" + source, new() { Tools = tools });
        }
        var catalog = new CapabilityDiscovery(new() { McpClientFactory = factory });
        var state = PlannerFixture.Session(); state.Request.Prompt = "RESUME entries";
        state.Request.Generation.MaxInputTokensPerRequest = 24000;
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"] };
        state.Discovery.Sources = (await catalog.ListSourcesAsync(PlannerFixture.Ct)).ToList();
        foreach (var source in state.Discovery.Sources)
            state.Discovery.Pages.Add(await catalog.ListAsync(source.Id, null, PlannerFixture.Ct, "RESUME entries"));
        var selected = HybridWorkflowPlanner.Shortlist(state);
        Assert.Equal(24, selected.Count);
        Assert.Equal(match == "name" ? "ReadRésuméEntries" : "utility_999", selected[0].Name);
        Assert.Equal(CapabilityDiscovery.SourceId("arbitrary_2"), selected[0].SourceId);
        Assert.All(state.Discovery.Pages, p => { Assert.Equal(8, p.Capabilities.Count); Assert.NotNull(p.NextCursor); });
        state.Discovery.Pages.Reverse();
        Assert.Equal(selected.Select(c => c.Id), HybridWorkflowPlanner.Shortlist(state).Select(c => c.Id));
    }

    [Fact]
    public void FixedOperationRepairOmitsUnusableDiscoveryButRetainsContractsAndLimitations()
    {
        var state = State(); state.Plan = new() { Root = new() { Tasks = [new() { Id = "use", Objective = "Use", Operation = "a0" }] } };
        state.RevisionScope = ["/tasks/use/inputs/value"];
        state.Diagnostics = [new("TASK_INPUT_TYPE", state.RevisionScope[0], "Fix this binding")];
        state.Discovery.Limitations = ["One source remains unavailable."];
        var before = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var prompt = HybridWorkflowPlanner.Prompt(state); var context = Context(prompt);
        Assert.Contains("schema-only-a0", prompt); Assert.DoesNotContain("schema-only-a1", prompt);
        Assert.DoesNotContain("A source description", prompt);
        Assert.All(context["coverage"]!.AsArray(), c => Assert.False(c!.AsObject().ContainsKey("index")));
        Assert.Contains("One source remains unavailable.", prompt);
        Assert.Equal(before, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
    }

    [Fact]
    public async Task CompletedRefinementIsSourceScopedAndSurvivesRecovery()
    {
        var state = State(); state.ModelCalls = 2;
        state.Usage = new() { Calls = 2, TotalTokens = 123, EstimatedCost = 0.1m };
        var catalog = new Pages(state.Discovery.Pages);
        var runtime = new TestRuntime { Capabilities = catalog };
        runtime.Proposal.Plan = null; runtime.Proposal.DiscoveryRequests = [new("b", Query: "OTHER")];
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Empty(state.Diagnostics); Assert.Equal("other", state.Discovery.Pages[^1].Query);
        Assert.Equal(3, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Equal(123, state.Usage!.TotalTokens);
        Assert.Equal(0.1m, state.Usage.EstimatedCost);
        var recovered = PlannerFixture.Clone(state);
        Assert.Equal("other", recovered.Discovery.Pages[^1].Query);
        Assert.All(PlanningDiscoveryContext.Candidates(recovered).Take(8), c => Assert.Equal("a", c.SourceId));
        Assert.True(PlanningDiscoveryContext.Candidates(recovered).FindIndex(c => c.Id == "b0") < PlanningDiscoveryContext.Candidates(recovered).FindIndex(c => c.Id == "c0"));
        Assert.Equal(4, recovered.Discovery.Pages.Count);
        var query = recovered.Discovery.PresentationQuery;
        runtime.Proposal.DiscoveryRequests = [new("b", Query: " other ")];
        recovered = await new HybridWorkflowPlanner().AdvanceAsync(recovered, new() { ExpectedRevision = recovered.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(recovered.Diagnostics, d => d.Code == "DISCOVERY_NO_PROGRESS");
        Assert.Equal(query, recovered.Discovery.PresentationQuery); Assert.Single(catalog.Reads);
    }

    [Fact]
    public void SmallExhaustedSourceStillAllowsRefinementOutsideTheGlobalShortlist()
    {
        var state = State();
        state.Discovery.Pages[1] = new("b", null, state.Discovery.Pages[1].Capabilities.Take(1).ToList(), null, Query: "find needle records");
        var wire = new JsonObject { ["requirements"] = JsonSerializer.SerializeToNode(PlannerFixture.Requirements(), PlanningJsonContext.Default.PlanningRequirements),
            ["discoveryRequests"] = new JsonArray(new JsonObject { ["sourceId"] = "b", ["cursor"] = null, ["query"] = "other" }), ["plan"] = null };
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, PlanningSchemas.Proposal(state)));
    }

    private sealed class Pages(List<CapabilityPage> pages) : ICapabilityCatalog
    {
        internal List<string> Reads = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null)
        { Reads.Add(sourceId); return Task.FromResult(pages.First(p => p.SourceId == sourceId) with { Cursor = cursor, Query = query }); }
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => throw new InvalidOperationException("Already resolved");
    }

    internal static PlanningSession State()
    {
        var state = PlannerFixture.Session(); state.Request.Prompt = "Find needle records";
        state.Request.Generation.MaxInputTokensPerRequest = 24000; state.Requirements = null;
        state.Catalog = new() { AllowedStepTypes = ["mcp.call"] };
        foreach (var source in new[] { "a", "b", "c" })
        {
            state.Discovery.Sources.Add(new(source, "A source description"));
            var capabilities = Enumerable.Range(0, 8).Select(i => new CapabilitySummary(source + i, source,
                source == "a" ? "needle_records_" + i : "other_" + i, "Utility", "mcp.call", "read", "v1", Operation: new()
                {
                    Id = source + i, Description = "schema-only-" + source + i,
                    Inputs = [new() { Name = "value", Schema = new() { ["type"] = "string" }, Required = true }],
                    Outputs = [new() { Name = "result", Schema = new() { ["type"] = "string" }, Required = true }]
                })).ToList();
            state.Discovery.Pages.Add(new(source, null, capabilities, "next-" + source, Query: "find needle records"));
            state.Discovery.Resolved.AddRange(capabilities.Select(c => new PlanningCapability { Id = c.Id, Version = c.Version, Operation = c.Operation }));
        }
        return state;
    }

    private static JsonNode Context(string prompt) => JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
}
