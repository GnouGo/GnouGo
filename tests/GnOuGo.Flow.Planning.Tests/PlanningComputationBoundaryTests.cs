using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningComputationBoundaryTests
{
    [Fact]
    public void FilteredArraysKeepKnownFieldsAndOpaqueMembers()
    {
        var args = new Dictionary<string, JsonObject> { ["rows"] = JsonNode.Parse("""{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"status":{"type":"string"},"raw":{"x-gnougo-opaque":true}},"required":["label","status","raw"],"additionalProperties":false}}""")!.AsObject() };
        PlanningComputationContracts.Validate("(() => { const selected = rows.filter(row => row.status === 'failed').map(row => row.label); return selected.length; })()", args);
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("rows.filter(row => true).map(row => row.raw.invented)", args));
    }

    [Fact]
    public void RegexMatchesPermitWholeCaptureValuesWithoutInventingCaptureFields()
    {
        var args = new Dictionary<string, JsonObject> { ["text"] = new() { ["type"] = "string" } };
        PlanningComputationContracts.Validate("(() => { const match = text.match(/^([^:]+):(.*)$/); if (!match) throw new Error('Invalid identifier'); return { first: match[1], second: match[2] }; })()", args);
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("(() => { const match = text.match(/(.*)/); return match[1].invented; })()", args));
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("text.match(/(.*)/).map(value => value.invented)", args));
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("((value) => value.invented)(text)", args));
    }
    [Theory]
    [InlineData("{\"x-gnougo-opaque\":true}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
    public void ArrayUnionsCannotHideOpaqueOrClosedObjectAlternatives(string alternative)
    {
        var args = new Dictionary<string, JsonObject> { ["value"] = new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }, JsonNode.Parse(alternative)) } };
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("value[0]", args));
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("value.invented", args));
    }
    [Fact]
    public void CallbackCollectionKeepsItsItemContractAndExtraArgumentsStayOpaque()
    {
        var args = new Dictionary<string, JsonObject> { ["rows"] = JsonNode.Parse("""{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"}},"required":["label"],"additionalProperties":false}}""")!.AsObject() };
        PlanningComputationContracts.Validate("rows.map((row, index, source) => source[index].label)", args);
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("rows.map((row, index, source) => source[index].invented)", args));
        Assert.Throws<InvalidOperationException>(() => PlanningComputationContracts.Validate("rows.map((row, index, source, absent) => absent.invented)", args));
    }
    private static GroundedPlan Calculation(string text) => new()
    {
        Inputs = [new("quantity", new() { Type = "number" }, false, new() { Kind = "number", Number = 4 })],
        Operations = [new CalculateGroundedOperation { Id = "total", Value = new() { Kind = "compute", Text = text,
            Members = [new("quantity", new() { Kind = "input", Source = "quantity" })] } }],
        Outputs = [new("total", new() { Kind = "result", Source = "total" })]
    };

    [Theory]
    [InlineData("const doubled = quantity * 2; return doubled;")]
    [InlineData("if (quantity > 0) { return quantity * 2; } return 0;")]
    public async Task SupportedComputationBodyReachesDeclaredContractFallback(string text)
    {
        var runtime = new TestRuntime(Calculation(text));
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.DoesNotContain(state.Diagnostics, d => d.Required);
        Assert.NotEmpty(state.Scenarios);
        Assert.Equal("number", PlanningGraphCompiler.ToJsonSchema(state.Graph!.Workflows.Single(w => w.Key == "main").Outputs.Single().Schema, state.Catalog!)["type"]!.ToString());
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("8", result.Outputs!["total"]!.ToJsonString());
    }

    [Fact]
    public async Task TypedCorrectionMaySupplyASupportedComputationBody()
    {
        var runtime = new TestRuntime(Calculation("quantity + unknown"));
        runtime.Respond = request =>
        {
            var props = request.StructuredOutputSchema!["properties"]!;
            var replan = props["operations"] is not null && props["summary"] is null;
            var phase = replan ? "replan" : props["actions"] is not null ? "semantic" : "binding";
            return GnOuGo.Planning.Examples.PlanningCorpus.FixtureResponse(request, phase, Calculation(replan ? "const doubled = quantity * 2; return doubled;" : "quantity + unknown"));
        };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(3, runtime.Calls.Count);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task HostContractDisagreementStopsBeforeFixtureCallsOrReplanning()
    {
        var runtime = new TestRuntime { Validation = [new("STEP_REFERENCE_NOT_AVAILABLE", "workflow:main/field:outputs.message", "Injected disagreement with validated lowering.")] };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, runtime.Calls.Count); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_HOST_CONTRACT");
        Assert.Null(state.Yaml); Assert.Empty(state.Scenarios);
    }

    [Fact]
    public async Task UnexpectedHostFailureStopsWithoutRepeatingOrSpendingModelRepairs()
    {
        var runtime = new TestRuntime { ValidationFailure = new NullReferenceException("Injected host validator defect.") };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Single(state.Diagnostics, d => d.Code == "PLANNING_INVALID");
        Assert.Null(state.Yaml);
        Assert.Null(state.ApprovedHash);
        var checkpoints = runtime.Checkpoints.Count;
        var next = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Same(state, next);
        Assert.Equal(checkpoints, runtime.Checkpoints.Count);
    }
}
