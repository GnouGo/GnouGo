using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactDiscoveryProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshDiscoveryOmitsOnlyEmptyTopLevelNavigation(bool instructions)
    {
        var (state, _) = await Ready();
        state.Request.Policy.Instructions = instructions ? "Preserve all original constraints." : "";
        state.Construction!.Evidence = new()
        {
            ["original"] = new JsonObject
            {
                ["empty"] = "", ["null"] = null, ["items"] = new JsonArray(),
                ["minimum"] = 0, ["additionalProperties"] = false,
                ["description"] = "Retain the entire original statement."
            }
        };
        var retained = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var fresh = PlanningCompactConstruction.DiscoveryContext(state);
        Assert.False(fresh.ContainsKey("sources"));
        Assert.False(fresh.ContainsKey("descriptors"));
        Assert.False(fresh.ContainsKey("diagnostics"));
        Assert.Equal(instructions, fresh.ContainsKey("instructions"));
        Assert.True(JsonNode.DeepEquals(state.Construction.Evidence, fresh["evidence"]));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        var untrimmed = fresh.DeepClone().AsObject();
        untrimmed["sources"] = new JsonArray();
        untrimmed["descriptors"] = new JsonArray();
        untrimmed["diagnostics"] = new JsonArray();
        if (!instructions) untrimmed["instructions"] = "";
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var before = PlanningRequestMeasurements.Measure(Complete(state, PlanningCompactConstruction.Request(
            PlanningCompactConstruction.DiscoveryGuidance(state), untrimmed, schema.DeepClone().AsObject(),
            PlanningCompactConstruction.DiscoveryPurpose)), PlanningCompactConstruction.DiscoveryPurpose, 3000);
        var after = PlanningRequestMeasurements.Measure(Complete(state, PlanningCompactConstruction.Request(
            PlanningCompactConstruction.DiscoveryGuidance(state), fresh, schema.DeepClone().AsObject(),
            PlanningCompactConstruction.DiscoveryPurpose)), PlanningCompactConstruction.DiscoveryPurpose, 3000);
        Assert.True(after.CompleteRequestBytes < before.CompleteRequestBytes);
        Assert.True(after.EstimatedInputTokens < before.EstimatedInputTokens);
        Assert.Equal(after.CompleteRequestBytes, after.Components.Sum(component => component.SerializedBytes));
    }

    [Fact]
    public async Task SavedDiscoveryWithEmptyNavigationResumesItsExactRequest()
    {
        var (state, runtime) = await Ready();
        state.Request.Policy.Instructions = "";

        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
            PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state),
            PlanningCompactConstruction.DiscoveryPurpose), out var evidenceAddresses);
        Assert.False(Context(request).ContainsKey("sources"));
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request, EvidenceAddresses = evidenceAddresses };
        runtime.CaptureIssued(state.PendingCall);
        state.ModelCalls++;

        Assert.False(PlanningCompactConstruction.DiscoveryContext(state).ContainsKey("sources"));
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
