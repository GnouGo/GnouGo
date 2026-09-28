using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DynamicDiscoveryTests
{
    [Fact]
    public void SpareBudgetAdmitsMoreThanEightExactContracts()
    {
        var state = GlobalDiscoveryTests.State();
        var selected = HybridWorkflowPlanner.Shortlist(state);
        Assert.Equal(24, selected.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "a" + i), selected.Take(8).Select(c => c.Id));
        Assert.InRange(PlanningJsonTransport.EstimateInputTokens(HybridWorkflowPlanner.BuildPrompt(state, selected), PlanningSchemas.Proposal(state)), 1, 21600);
    }

    [Fact]
    public void OversizedCandidateDoesNotHideSmallerCandidatesOrTrimItsContract()
    {
        var state = GlobalDiscoveryTests.State();
        var large = state.Discovery.Pages[0].Capabilities[0];
        large.Operation!.Inputs[0].Schema["description"] = new string('x', 72000);
        var selected = HybridWorkflowPlanner.Shortlist(state);
        Assert.DoesNotContain(selected, c => c.Id == large.Id);
        Assert.Equal(23, selected.Count);
        Assert.Equal(72000, large.Operation.Inputs[0].Schema["description"]!.ToString().Length);
        Assert.Contains(PlanningDiscoveryContext.Candidates(state), c => c.Id == large.Id);
        Assert.InRange(PlanningJsonTransport.EstimateInputTokens(HybridWorkflowPlanner.BuildPrompt(state, selected), PlanningSchemas.Proposal(state)), 1, 21600);
    }

    [Fact]
    public async Task MandatoryContextAboveTheSoftTargetStillUsesTheSavedHardAllowance()
    {
        var state = GlobalDiscoveryTests.State(); state.Requirements = PlannerFixture.Requirements();
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "use", Operation = "a0", Objective = "Use the selected operation" }] } };
        state.Discovery.Pages = [state.Discovery.Pages[0] with { Capabilities = [state.Discovery.Pages[0].Capabilities[0]] }];
        state.Discovery.Resolved.RemoveAll(c => c.Id != "a0");
        var mandatory = PlanningJsonTransport.EstimateInputTokens(HybridWorkflowPlanner.BuildPrompt(state, []), PlanningSchemas.Proposal(state));
        state.Request.Generation.MaxInputTokensPerRequest = mandatory + 100;
        var runtime = new TestRuntime { Respond = (_, _) => throw new IOException("Retain pending request") };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        var request = Assert.Single(runtime.Calls);
        Assert.Equal(mandatory, PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()));
        Assert.True(mandatory > state.Request.Generation.MaxInputTokensPerRequest * 0.9);
        Assert.Contains("schema-only-a0", request.Prompt); Assert.DoesNotContain("schema-only-a1", request.Prompt);
        Assert.Equal(1, state.ModelCalls); Assert.DoesNotContain(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT");
    }

    [Fact]
    public void RefinementAddsTermsOnlyForItsSourceAndCannotReplaceAcceptedIntent()
    {
        var state = GlobalDiscoveryTests.State();
        state.Requirements = new() { Summary = "Needle records", Outcomes = [new("records", "Find records")] };
        state.Request.Prompt = "Find";
        state.Discovery.PresentationQuery = "other"; // Historical global override is not authority.
        state.Discovery.Pages.Add(state.Discovery.Pages[1] with { Query = "other", Cursor = "refined" });
        var ranked = PlanningDiscoveryContext.Candidates(state);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "a" + i), ranked.Take(8).Select(c => c.Id));
        Assert.True(ranked.FindIndex(c => c.Id == "b0") < ranked.FindIndex(c => c.Id == "c0"));
        var before = ranked.Select(c => c.Id).ToArray();
        state.Discovery.PresentationQuery = "unrelated replacement";
        Assert.Equal(before, PlanningDiscoveryContext.Candidates(PlannerFixture.Clone(state)).Select(c => c.Id));
    }

    [Fact]
    public void DuplicateQueryTermsDoNotAmplifySourceScores()
    {
        var state = GlobalDiscoveryTests.State();
        state.Discovery.Pages.Add(state.Discovery.Pages[1] with { Query = "needle needle records other OTHER", Cursor = "refined" });
        var before = PlanningDiscoveryContext.Candidates(state).Select(c => c.Id).ToArray();
        state.Discovery.Pages[^1] = state.Discovery.Pages[^1] with { Query = "other" };
        Assert.Equal(before, PlanningDiscoveryContext.Candidates(state).Select(c => c.Id));
    }

    [Fact]
    public async Task UnavailableOptionalContractLeavesRoomForRemainingCandidates()
    {
        var state = GlobalDiscoveryTests.State();
        var all = state.Discovery.Resolved.ToArray(); state.Discovery.Resolved.Clear();
        var catalog = new UnavailableFirst(all);
        var runtime = new TestRuntime { Capabilities = catalog, Respond = (_, _) => throw new IOException("Retain pending request") };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal(24, catalog.Reads.Count); Assert.Equal(24, catalog.Reads.Distinct().Count());
        Assert.Equal(23, state.Discovery.Resolved.Count);
        Assert.Contains(state.Discovery.Limitations, l => l.StartsWith("a0:", StringComparison.Ordinal));
        var request = Assert.Single(runtime.Calls);
        Assert.DoesNotContain("schema-only-a0", request.Prompt);
        Assert.Contains("schema-only-c7", request.Prompt);
        Assert.InRange(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()), 1, 21600);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    private sealed class UnavailableFirst(PlanningCapability[] capabilities) : ICapabilityCatalog
    {
        internal List<string> Reads = [];
        public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => throw new InvalidOperationException();
        public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null) => throw new InvalidOperationException();
        public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct)
        {
            Reads.Add(summary.Id);
            return summary.Id == "a0" ? throw new IOException("Unavailable contract") : Task.FromResult(capabilities.Single(c => c.Id == summary.Id));
        }
    }
}
