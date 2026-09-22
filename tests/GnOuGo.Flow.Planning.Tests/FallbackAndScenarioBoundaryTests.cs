using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FallbackAndScenarioBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvocationFallbackMustPreserveItsAuthoritativeResultContract(bool valid)
    {
        var runtime = new TestRuntime(mcp: PlannerFixture.Factory(1));
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, TestContext.Current.CancellationToken);
        var plan = new GroundedPlan
        {
            Operations = [new InvokeGroundedOperation { Id = "fetch", Capability = catalog.Capabilities.Single().Id,
                Fallback = valid ? new() { Kind = "object", Members = [new("value", new() { Kind = "number", Number = 0 })] } : new() { Kind = "null" } }],
            Outputs = [new("result", new() { Kind = "result", Source = "fetch" })]
        };
        var original = catalog.Capabilities.Single().OutputSchema.DeepClone();
        var validation = GroundedPlanValidator.Validate(plan, catalog);
        if (valid)
        {
            Assert.NotNull(validation.Plan);
            var graph = PlanningGraphBuilder.Build(validation.Plan);
            Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
            _ = new PlanningGraphCompiler().Compile(graph, catalog);
        }
        else
        {
            Assert.Null(validation.Plan);
            Assert.Contains(validation.Diagnostics, d => d.Location == "/scopes/main/operations/fetch/fallback" && d.Code == "GROUNDED_CONTRACT_INVALID");
        }
        Assert.True(JsonNode.DeepEquals(original, catalog.Capabilities.Single().OutputSchema));
    }

    [Fact]
    public async Task PropagatedScenarioFailureKeepsTheBusinessCauseAndFailedEvidence()
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        server.Tools.Add(new() { Name = "arbitrary_effect", EffectKind = "write",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""") });
        factory.RegisterServer("renamed_provider", server);
        var runtime = new TestRuntime(mcp: factory); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        state.GroundedPlan = new()
        {
            Operations = [new ValidateGroundedOperation { Id = "validate_value", Value = new() { Kind = "string", Text = "invalid" }, ResultType = new() { Type = "integer" } },
                new InvokeGroundedOperation { Id = "effect", After = ["validate_value"], Capability = state.Catalog.Capabilities.Single().Id }]
        };
        state.Graph = PlannerFixture.Build(state.GroundedPlan, state.Catalog);
        await PlanningValidationPipeline.ValidateAsync(state, runtime, TestContext.Current.CancellationToken);
        Assert.Contains(state.Scenarios, s => s.Outcome != "passed");
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        var evidenceCount = state.Diagnostics.Count;
        var findings = PlanningDiagnosticLocations.ForIntent(state);
        Assert.DoesNotContain(findings, d => d.Code == "PLANNING_HOST_CONTRACT");
        Assert.Contains(findings, d => d.Code == "SCENARIO_EXECUTION_FAILED" && d.Location == "/operations/0");
        Assert.Equal(evidenceCount, state.Diagnostics.Count);
        // Without a failed callee in the same scenario, a generated call failure
        // remains a host disagreement; unrelated failures cannot hide it.
        state.Scenarios.Clear();
        Assert.Contains(PlanningDiagnosticLocations.ForIntent(state), d => d.Code == "PLANNING_HOST_CONTRACT");
    }
}
