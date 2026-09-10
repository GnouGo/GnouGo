using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;
using static GnOuGo.Flow.Planning.Tests.HoleSessionTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DeterministicBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopCollectionUsesInputProvenanceWhileBodyOperationsRemainSeparate(bool externalDependency)
    {
        var behavior = new PlanningBehaviorPlan { Workflows = [new() { Key = "main", Purpose = "Process items", Inputs = [new("records", "Collection", true)],
            Steps = [new() { Key = "repeat", Kind = "loop", CapabilityId = "iterate", OperationIds = ["iteration"], InputDependencies = ["records"], Purpose = "Process every item",
                Steps = [new() { Key = "call", Kind = "workflow", WorkflowKey = "child", Purpose = "Process the item", InputDependencies = ["records"] }] }] },
            new() { Key = "child", Purpose = "Process one item", OperationIds = ["child_operation"], Inputs = [new("record", "Item", true)] }] };
        var state = Ready(behavior);
        state.Preparation!.Capabilities.Add(new() { Id = "iterate", Resolution = "local", StepType = "set", OperationIds = ["iteration"],
            InputOperationIds = [externalDependency ? "external_observation" : "child_operation"] });
        state.Graph!.Workflows[0].Inputs[0].Schema = new() { Type = "array", Items = new() { Type = "string" } };
        state.Graph.Workflows[1].Inputs[0].Schema = new() { Type = "string" };
        PlanningDataflowResolver.Resolve(state);
        var hole = Assert.Single(state.Construction.Holes, h => h.NodeKey == "repeat" && h.Kind == "value");
        var selected = PlanningBindingResolution.Unique(state, state.Graph.Workflows[0], hole);
        if (externalDependency) Assert.Null(selected);
        else { Assert.NotNull(selected); Assert.Equal("input", selected.Value.Kind); Assert.Equal("records", selected.Value.Source); }
    }

    [Fact]
    public void StructuredDecisionContractPropagatesWithoutReplacingTheRawProducerEnvelope()
    {
        var state = Ready(); var workflow = state.Graph!.Workflows[0]; var node = workflow.Steps[0];
        node.Type = "mcp.call"; node.CapabilityId = "producer"; node.OperationIds = ["observe"];
        node.StructuredOutput = new(new() { Type = PlanningGraphSkeleton.Unresolved });
        state.Preparation!.Capabilities.Add(new() { Id = "producer", StepType = "mcp.call", OutputSchema = new() { ["type"] = "object",
            ["properties"] = new JsonObject { ["raw"] = new JsonObject { ["type"] = "string" } } } });
        state.Preparation.Decisions.Add(new() { SourceCapabilityId = "producer", SourceOperationId = "observe", ContractSource = "structured_output",
            SourcePointer = "/json/outcome", ResponseSchema = new() { ["type"] = "string", ["enum"] = new JsonArray("ALLOW", "NONE") } });
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, node, "/workflows/0/steps/0/structuredOutput/schema", "schema", "Declared decision result");
        Assert.True(PlanningSchemaPropagation.Resolve(state, workflow));
        var contract = PlanningGraphCompiler.ToJsonSchema(state.Graph!.Workflows[0].Steps[0].StructuredOutput!.Schema, state.Preparation);
        Assert.Equal("string", contract["properties"]!["outcome"]!["type"]!.ToString());
        Assert.Null(contract["properties"]!["raw"]);
        Assert.NotNull(state.Preparation.Capabilities.Single().OutputSchema["properties"]!["raw"]);
        Assert.True(Assert.Single(state.Construction.Holes).Resolved);
    }

    [Fact]
    public async Task DeclaredFixedValuesAndUniqueOutputRequireNoExecutableModelCalls()
    {
        var state = Ready(); state.Request.Baseline = new() { Workflows = [FakeRuntime.ExecutableWorkflow()] };
        state.Preparation!.Capabilities.Add(new()
        {
            Id = "fixed", StepType = "set", EffectKind = "none", FixedInput = new() { ["message"] = "Hello" },
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""")!.AsObject()
        });
        state.BehaviorPlan!.Workflows[0].Steps[0].CapabilityId = "fixed";
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        PlanningGraphSkeleton.Create(state); PlanningDataflowResolver.Resolve(state);
        var runtime = new FakeRuntime();
        for (var i = 0; i < 12 && !PlanningStatus.IsWaiting(state.Status); i++) state = await Advance(state, runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(runtime.Phases, p => p == PlanningPhase.Construction);
        Assert.All(state.Construction.Holes, h => Assert.True(h.Resolved));
        Assert.Equal(0, Assert.Single(state.Construction.Workflows).Calls);
    }

    [Fact]
    public void AmbiguousSourcesRemainHolesAndPreserveDeclaredDefaultsAndNullability()
    {
        var state = Ready();
        var first = FakeRuntime.ExecutableWorkflow().Steps[0]; first.Key = "first";
        var second = FakeRuntime.ExecutableWorkflow().Steps[0]; second.Key = "second";
        var graph = state.Graph!; var workflow = graph.Workflows[0];
        workflow.Steps = [first, second]; workflow.Outputs[0].Schema = new() { Type = "string" };
        workflow.Inputs = [new() { Name = "threshold", Required = false, Schema = new() { Type = "number", Nullable = false }, Default = new() { Kind = "number", Number = 100 } }];
        state.Construction.Holes = state.Construction.Holes.Where(h => h.Path.EndsWith("/outputs/0/value", StringComparison.Ordinal)).ToList();
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.False(Assert.Single(state.Construction.Holes).Resolved);
        Assert.Equal(100, state.Graph!.Workflows[0].Inputs[0].Default!.Number);
        Assert.False(state.Graph.Workflows[0].Inputs[0].Schema.Nullable);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("none")]
    public async Task PureOutcomeProjectionExecutesForSelectedAndNoActionBranches(string outcome)
    {
        var behavior = BehaviorPlan(); behavior.Workflows[0].Steps = [new() { Key = "choose", Kind = "decision", Purpose = "Select a result", InputDependencies = [],
            Outcomes = [new("yes", "Selected", false, []), new("none", "No action", true, [])] }];
        var state = Ready(behavior); var workflow = state.Graph!.Workflows[0];
        workflow.Inputs = [new() { Name = "choice", Schema = new() { Type = "string", Enum = ["yes", "none"] } }];
        workflow.Steps[0].Expr = new() { Kind = "input", Source = "choice" };
        workflow.Outputs[0].Schema = new() { Type = "string" };
        workflow.Outputs[0].Value = new() { Kind = "output", Source = workflow.Steps.Single(n => n.InternalRole == "branch_result").Key, Path = ["outcome"] };
        var yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Preparation!);
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml));
        var result = await new GnOuGo.Flow.Core.Runtime.WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["choice"] = outcome }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(outcome, result.Outputs!["message"]!.ToString());
    }

    [Fact]
    public void DecisionAdaptersHaveDeterministicFrozenIdentitiesAndNoAuthority()
    {
        var behavior = BehaviorPlan(); behavior.Workflows[0].Steps = [new() { Key = "choose", Kind = "decision", Purpose = "Select a result", InputDependencies = [],
            Outcomes = [new("yes", "Selected", false, []), new("none", "No action", true, [])] }];
        var first = Ready(behavior); var second = Ready(behavior);
        Assert.Equal(first.Construction.SkeletonFingerprint, second.Construction.SkeletonFingerprint);
        var adapters = PlanningGraphCompiler.Enumerate(first.Graph!.Workflows[0].Steps).Where(n => n.InternalRole is not null).ToArray();
        Assert.Equal(3, adapters.Length);
        Assert.All(adapters, n => { Assert.Null(n.CapabilityId); Assert.Empty(n.OperationIds); Assert.Equal("set", n.Type); });
        adapters[0].Input.Members[0].Value.Text = "invented";
        Assert.NotEqual(first.Construction.SkeletonFingerprint, PlanningGraphSkeleton.Fingerprint(first.Graph));
    }
}
