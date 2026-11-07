using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessProjectionPublicResultsTests
{
    [Fact]
    public async Task InternalProducerResultsDoNotInventPublicDeclarationsOrHideOriginals()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningBusinessProjection.Build(state, global: true);
        Assert.Empty(context["declaredPublicOutputs"]!.AsArray());
        Assert.Empty(context["publicOutputs"]!.AsArray());
        Assert.NotEmpty(context["actions"]![0]!["outputs"]!.AsArray());
        Assert.NotEmpty(context["requirements"]!.AsArray());
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Null(PlanningBusinessProjection.Build(state, global: false)["declaredPublicOutputs"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDifferentActualResultKeepsTheExactDeclaredInterfaceVisible(bool differentBinding)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        state.Requirements!.Outputs = [new() { Name = "receipt", Required = true, Type = new() { Kind = "object", Fields =
        [new() { Name = "status", Required = true, Type = new() { Kind = "string", Enum = ["written", "failed"] } },
         new() { Name = "location", Required = false, Type = new() { Kind = "string", Nullable = true }, Default = new() { Kind = "null" } }] } }];
        if (differentBinding)
            state.Construction!.Candidate!.Plan.Root.Outputs.Add(new("receipt", new() { Kind = "output", Source = "compute", Port = "result" }));
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var context = PlanningBusinessProjection.Build(state, global: true);
        var declaration = Assert.Single(context["declaredPublicOutputs"]!.AsArray())!;
        Assert.Equal("receipt", declaration["name"]!.ToString());
        Assert.True(declaration["required"]!.GetValue<bool>());
        var fields = declaration["type"]!["fields"]!.AsArray();
        Assert.Equal(new[] { "written", "failed" }, fields[0]!["type"]!["enum"]!.AsArray().Select(v => v!.ToString()));
        Assert.True(fields[1]!["type"]!["nullable"]!.GetValue<bool>());
        Assert.False(fields[1]!["required"]!.GetValue<bool>());
        Assert.Equal("null", fields[1]!["default"]!["kind"]!.ToString());
        var actual = context["publicOutputs"]!.AsArray();
        if (differentBinding) Assert.Equal("compute", Assert.Single(actual)!["value"]!["source"]!.ToString());
        else Assert.Empty(actual);
        var finding = new JsonObject { ["decision"] = new JsonObject { ["verdict"] = "needs_change", ["reason"] = "The actual result does not implement the declared caller receipt.",
            ["obligations"] = new JsonArray("work"), ["targets"] = new JsonArray("/root") } };
        Assert.Empty(PlanningModelCalls.ValidateResponseFindings(finding, HybridWorkflowPlanner.ConstructionReviewSchema(context)));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }
}
