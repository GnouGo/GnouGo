using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class AcceptedOutputTests
{
    [Fact]
    public void IssuedOutputContractIsDetectedOnlyThroughTheRequirementsProperty()
    {
        Assert.True(PlanningSchemas.DeclaresOutputs(PlanningSchemas.FullProposal(PlannerFixture.Session())));
        var historical = JsonNode.Parse("""{"properties":{"requirements":{"$ref":"#/$defs/intent"}},"$defs":{"intent":{"properties":{"summary":{},"outcomes":{}}},"operation":{"properties":{"summary":{},"outcomes":{},"outputs":{}}}}}""");
        Assert.False(PlanningSchemas.DeclaresOutputs(historical));
    }

    [Theory]
    [InlineData("message", "string", true)]
    [InlineData("missing", "string", false)]
    [InlineData("message", "array", false)]
    public async Task AcceptedBusinessResultsAreCheckedAgainstTheCompiledInterface(string name, string kind, bool valid)
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Requirements!.Outputs = [new() { Name = name, Type = new() { Kind = kind, Items = kind == "array" ? new() { Kind = "string" } : null } }];
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(valid ? PlanningStatus.FinalReview : PlanningStatus.Stopped, state.Status);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        if (valid) PlanningArtifactApproval.Verify(state);
        else Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED");
    }

    [Fact]
    public async Task OutputDeclarationIsImmutableUntilExplicitRevisionAndApprovalChecksItAgain()
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Requirements!.Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }];
        var state = await PlannerFixture.RunAsync(runtime);
        var saved = PlannerFixture.Clone(state); var hash = state.ComputeArtifactHash();
        state.Graph!.Workflows[0].Outputs.Clear();
        Assert.ThrowsAny<Exception>(() => PlanningArtifactApproval.Verify(state));
        var revised = await new HybridWorkflowPlanner().AdvanceAsync(saved, new() { Kind = "revise", ExpectedRevision = saved.Revision, Text = "Return a second business result" }, runtime, PlannerFixture.Ct);
        Assert.Null(revised.Requirements); Assert.Null(revised.ApprovedHash); Assert.Null(revised.Yaml);
        Assert.Contains("outputs", revised.Request.RevisionContext!);
        Assert.Equal(1, revised.ModelCalls); Assert.NotEqual(hash, revised.ComputeArtifactHash());
    }

    [Fact]
    public void LegacyRequirementsOmitTheNewFieldWithoutChangingTheirSerializedShape()
    {
        var requirements = PlannerFixture.Requirements();
        Assert.DoesNotContain("outputs", JsonSerializer.Serialize(requirements, PlanningJsonContext.Default.PlanningRequirements));
        var state = PlannerFixture.Session(); state.Requirements = requirements;
        Assert.Empty(PlanningClarifications.OutputFindings(state, PlannerFixture.Greeting()));
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
