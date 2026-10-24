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
        var historicalState = PlannerFixture.Clone(state);
        historicalState.Construction!.Compact!.PresentationVersion = null;
        var historical = PlanningCompactConstruction.DiscoveryContext(historicalState);

        Assert.False(fresh.ContainsKey("sources"));
        Assert.False(fresh.ContainsKey("descriptors"));
        Assert.False(fresh.ContainsKey("diagnostics"));
        Assert.Equal(instructions, fresh.ContainsKey("instructions"));
        Assert.True(historical.ContainsKey("sources"));
        Assert.True(historical.ContainsKey("descriptors"));
        Assert.True(historical.ContainsKey("diagnostics"));
        Assert.True(historical.ContainsKey("instructions"));
        foreach (var field in new[] { "requirements", "globalConstraints", "evidence" })
            Assert.True(JsonNode.DeepEquals(historical[field], fresh[field]), field);
        Assert.True(JsonNode.DeepEquals(state.Construction.Evidence, fresh["evidence"]));
        Assert.Equal(retained, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));

        // Measure the same complete schema/envelope, varying only this context.
        // Historical step and directory projections intentionally differ.
        var untrimmed = fresh.DeepClone().AsObject();
        foreach (var (key, value) in historical.Where(entry => !fresh.ContainsKey(entry.Key)))
        {
            Assert.True(value is null or JsonArray { Count: 0 } ||
                value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length == 0, key);
            untrimmed[key] = value?.DeepClone();
        }
        var schema = PlanningCompactConstruction.DiscoverySchema(state);
        var before = PlanningRequestMeasurements.Measure(Complete(state, PlanningCompactConstruction.Request(
            PlanningCompactConstruction.DiscoveryInstructions, untrimmed, schema.DeepClone().AsObject(),
            PlanningCompactConstruction.DiscoveryPurpose)), PlanningCompactConstruction.DiscoveryPurpose, 3000);
        var after = PlanningRequestMeasurements.Measure(Complete(state, PlanningCompactConstruction.Request(
            PlanningCompactConstruction.DiscoveryInstructions, fresh, schema.DeepClone().AsObject(),
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
        state.Construction!.Compact!.PresentationVersion = null;
        var request = Complete(state, PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryInstructions,
            PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state),
            PlanningCompactConstruction.DiscoveryPurpose));
        Assert.True(Context(request).ContainsKey("sources"));
        state.PendingCall = new() { Id = request.ClientRequestId!, Purpose = PlanningCompactConstruction.DiscoveryPurpose, Request = request };
        state.ModelCalls++;
        state.Construction.Compact.PresentationVersion = 2;
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
