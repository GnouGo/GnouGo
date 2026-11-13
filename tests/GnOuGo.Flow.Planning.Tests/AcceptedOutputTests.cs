using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AcceptedOutputTests
{
    [Theory]
    [InlineData("message", "string", true)]
    [InlineData("missing", "string", false)]
    [InlineData("message", "array", false)]
    public async Task AcceptedBusinessResultsAreCheckedAgainstTheCompiledInterface(string name, string kind, bool valid)
    {
        var state = PlannerFixture.Session();
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Requirements = new() { Outputs = [new() { Name = name, Type = new() { Kind = kind, Items = kind == "array" ? new() { Kind = "string" } : null } }] };
        var graph = PlannerFixture.Greeting(); graph.Workflows[0].Outputs[0].Schema = new() { Type = "string" };
        var findings = PlanningClarifications.OutputFindings(state, graph);
        Assert.Equal(valid, findings.Count == 0);
        if (!valid) Assert.Contains(findings, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED");
    }

    [Fact]
    public async Task ReviewedOutputDeclarationIsBoundIntoApproval()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("test", "public-output-approval", null, PlannerFixture.Ct);
        var saved = PlannerFixture.Clone(state); var hash = state.ComputeArtifactHash();
        state.Graph!.Workflows[0].Outputs.Clear();
        Assert.ThrowsAny<Exception>(() => PlanningArtifactApproval.Verify(state));
        PlanningArtifactApproval.Verify(saved);
        Assert.Equal(hash, saved.ComputeArtifactHash());
    }


    [Fact]
    public void OptionalOutputsMayBeAbsentButNullabilityAndExtraOutputsAreChecked()
    {
        var state = PlannerFixture.Session(); state.Catalog = new(); state.Requirements = PlannerFixture.Requirements();
        state.Requirements.Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }, new() { Name = "optional", Type = new() { Kind = "string" }, Required = false }];
        var graph = PlannerFixture.Greeting(); graph.Workflows[0].Outputs[0].Schema = new() { Type = "string" };
        Assert.Empty(PlanningClarifications.OutputFindings(state, graph));
        state.Requirements.Outputs.Clear();
        Assert.Single(PlanningClarifications.OutputFindings(state, graph));
    }
}
