using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class NestedControlGuidanceTests
{
    [Theory]
    [InlineData("sequence")]
    [InlineData("conditional")]
    [InlineData("parallel")]
    [InlineData("group")]
    public async Task EveryReachableNestedLoopReceivesItsExportAndCollectionContract(string kind)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = kind;
        step.CloseRequirementIds.Clear();
        var schema = PlanningCompactConstruction.DraftSchema(state);
        Assert.Contains(Objects(schema), node => node["properties"]?["kind"]?["enum"] is JsonArray values && values.Any(value => value?.ToString() == "foreach"));
        var guidance = schema["description"]!.ToString();
        Assert.Contains("Each foreach body output becomes an array on the enclosing task", guidance, StringComparison.Ordinal);
        Assert.Contains("array-valued outputs remain nested", guidance, StringComparison.Ordinal);
        Assert.Contains("preserving input order and repetitions", guidance, StringComparison.Ordinal);
        Assert.Contains("outputs export values across the enclosing task/group boundary", guidance, StringComparison.Ordinal);
        Assert.Contains("both alternatives must export matching names, types, nullability and requiredness", guidance, StringComparison.Ordinal);
        Assert.Contains("Parallel sibling branches are independent", guidance, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("transform")]
    public async Task ScalarFragmentsDoNotReceiveUnreachableNestedControlGuidance(string kind)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        var step = state.Construction.Compact!.CurrentStep!;
        step.Kind = kind;
        step.CloseRequirementIds.Clear();
        var guidance = PlanningCompactConstruction.DraftSchema(state)["description"]!.ToString();
        Assert.DoesNotContain("Each foreach body output", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("Parallel sibling branches", guidance, StringComparison.Ordinal);
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var child in obj.SelectMany(pair => Objects(pair.Value))) yield return child;
        }
        else if (node is JsonArray array)
            foreach (var child in array.SelectMany(Objects)) yield return child;
    }
}
