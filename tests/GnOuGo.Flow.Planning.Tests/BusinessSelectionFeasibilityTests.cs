using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessSelectionFeasibilityTests
{
    [Theory]
    [InlineData("unavailable", "unavailable")]
    [InlineData("incomplete", "uninspected")]
    [InlineData("different_query", "uninspected")]
    [InlineData("completed", null)]
    [InlineData("recovered", null)]
    [InlineData("model_claim", null)]
    public async Task NextBusinessSelectionRetainsOnlyRecordedDiscoveryStatus(string observation, string? expected)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Null(state.Construction!.Compact!.CurrentBlockId);
        state.Discovery.Pages.Clear();
        state.Construction.Compact.RejectedSteps.Add(new JsonObject { ["reason"] = "MODEL_UNPROVEN: the requested business result is impossible" });
        state.Discovery.Limitations.Add("PRIVATE_RAW_LIMITATION");
        if (observation is "unavailable" or "recovered")
            state.Discovery.Pages.Add(new("PRIVATE_SOURCE", null, [], null, "PRIVATE_RAW_REASON", Query: "PRIVATE_QUERY"));
        if (observation is "incomplete" or "completed" or "different_query")
            state.Discovery.Pages.Add(new("PRIVATE_SOURCE", null, [], "PRIVATE_CURSOR", Query: "PRIVATE_QUERY"));
        if (observation is "completed" or "different_query")
            state.Discovery.Pages.Add(new("PRIVATE_SOURCE", "PRIVATE_CURSOR", [], null, Query: observation == "completed" ? "PRIVATE_QUERY" : "OTHER_QUERY"));
        if (observation == "recovered")
            state.Discovery.Pages.Add(new("PRIVATE_SOURCE", null, [], null, Query: "PRIVATE_QUERY"));
        var baseline = PlanningConstruction.Hash(state.Plan);
        var restored = PlannerFixture.Clone(state);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.SelectPurpose, purpose);
            var context = BusinessProjectionDeclarationsTests.Context(request);
            if (expected is null) Assert.False(context.ContainsKey("feasibilityObservations"));
            else
            {
                var fact = Assert.Single(Assert.IsType<JsonArray>(context["feasibilityObservations"]))!.ToString();
                Assert.Contains(expected, fact, StringComparison.Ordinal);
                Assert.Contains("do not establish whether the required capabilities exist", fact, StringComparison.Ordinal);
            }
            foreach (var excluded in new[] { "PRIVATE_SOURCE", "PRIVATE_RAW_REASON", "PRIVATE_RAW_LIMITATION", "PRIVATE_QUERY", "PRIVATE_CURSOR", "MODEL_UNPROVEN" })
                Assert.DoesNotContain(excluded, request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("operationIds", request.StructuredOutputSchema!.ToJsonString(), StringComparison.Ordinal);
            return new() { Json = new JsonObject
            {
                ["block"] = new JsonObject
                {
                    ["objective"] = "Produce the later independent result", ["requirementIds"] = new JsonArray("later"),
                    ["informationNeeds"] = new JsonArray(), ["expectedResults"] = new JsonArray("The later result"),
                    ["dependencies"] = new JsonArray(), ["successCriteria"] = new JsonArray("The later result is present")
                },
                ["evidenceRequests"] = null, ["clarifications"] = null
            } };
        };
        state = await BusinessReviewProtocolTests.Advance(restored, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(baseline, PlanningConstruction.Hash(state.Plan));
        Assert.Equal("Produce the later independent result", PlanningCompactConstruction.CurrentBlock(state)!.Objective);
    }
}
