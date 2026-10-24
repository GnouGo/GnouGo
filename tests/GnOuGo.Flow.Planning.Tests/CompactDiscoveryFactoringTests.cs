using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("shared", "shared")]
    [InlineData("first-next", "second-next")]
    [InlineData(null, "second-next")]
    public async Task FactoredProviderActionsPreserveExactSourceCursorPairs(string? first, string? second)
    {
        var (state, _) = await Ready();
        state.Discovery.Sources.Add(new("beta", "Another original provider."));
        if (first is not null) state.Discovery.Pages.Add(new("alpha", null, [], first, Query: Objective));
        if (second is not null) state.Discovery.Pages.Add(new("beta", null, [], second, Query: Objective));
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var fresh = PlanningCompactConstruction.DiscoverySchema(state);
        var historicalState = PlannerFixture.Clone(state);
        historicalState.Construction!.Compact!.PresentationVersion = null;
        var historical = PlanningCompactConstruction.DiscoverySchema(historicalState);

        Assert.Empty(PlanningContractValidation.ValidateSchema(fresh, strict: true));
        foreach (var source in new[] { "alpha", "beta", "unissued" })
        foreach (var cursor in new[] { first, second, null, "unissued", "page:sources:0" }.Distinct())
        foreach (var query in new[] { (string?)null, Objective })
        {
            var response = Discover(source, cursor, query);
            var expected = query is null && (source == "alpha" && cursor == first || source == "beta" && cursor == second);
            Assert.Equal(expected, PlanningContractValidation.ValidateInstance(response, fresh).Count == 0);
            Assert.Equal(expected, PlanningContractValidation.ValidateInstance(response, historical).Count == 0);
        }
        if (first == second)
        {
            Assert.True(fresh.ToJsonString().Length < historical.ToJsonString().Length);
            var items = fresh["properties"]!["discoveryRequests"]!["anyOf"]![0]!["items"]!;
            Assert.Null(items["anyOf"]);
            Assert.Equal(2, items["properties"]!["sourceId"]!["enum"]!.AsArray().Count);
        }
        Assert.Equal(original, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task UnavailableProviderIsNotAddedToAnEquivalentCursorBranch()
    {
        var (state, _) = await Ready();
        state.Discovery.Sources.Add(new("beta", "Another provider."));
        state.Discovery.Pages.Add(new("alpha", null, [], null, Query: Objective));
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("alpha", null), schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Discover("beta", null), schema));
        state.Discovery.Pages.Add(new("beta", null, [], null, Query: Objective));
        schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Discover("beta", null), schema));
    }

    [Fact]
    public async Task PreviouslyIssuedUnfactoredDiscoveryResumesUnchanged()
    {
        var (state, runtime) = await Ready();
        state.Discovery.Sources.Add(new("beta", "Another provider."));
        state.Construction!.Compact!.PresentationVersion = null;
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        Assert.Equal(2, schema["properties"]!["discoveryRequests"]!["anyOf"]![0]!["items"]!["anyOf"]!.AsArray().Count);
        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            PlanningCompactConstruction.DiscoveryContext(state), schema, PlanningCompactConstruction.DiscoveryPurpose));
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request };
        state.ModelCalls++;
        // New presentation availability must not replace a durably issued request.
        state.Construction.Compact.PresentationVersion = 2;
        var issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        var calls = state.ModelCalls;
        var plan = PlanningConstruction.Hash(state.Plan);
        runtime.Respond = (actual, _) =>
        {
            Assert.Equal(issued, JsonSerializer.Serialize(actual, PlanningJsonContext.Default.LLMRequest));
            return Wire(actual, Evidence("page:sources:0"));
        };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Null(state.PendingCall);
    }
}
