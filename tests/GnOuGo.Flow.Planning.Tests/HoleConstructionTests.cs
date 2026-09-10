using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class HoleConstructionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task TruncatedResponseRetriesConsumeTheDurableResponseAllowance(int limit)
    {
        var state = Ready(); state.Request.MaxRepairsPerWorkflowGate = limit;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        for (var attempt = 0; attempt < limit + 2; attempt++)
        {
            state = PlanningContext.Clone(state); state.Status = PlanningStatus.Generating;
            await new PlanningWorkflowConstruction().AdvanceAsync(state, runtime, TestContext.Current.CancellationToken);
        }
        Assert.Equal(limit + 1, runtime.Requests.Count);
        Assert.Equal(limit, PlanningRepairAllowances.Get(state, "main", PlanningGates.Response).Attempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPAIR_EXHAUSTED");
        Assert.Empty(state.Construction.Candidates);
    }

    [Fact]
    public void BaselinePureConstantReuseRequiresAnUnchangedProducerAndMatchingContract()
    {
        var state = Ready(); var workflow = state.Graph!.Workflows[0]; var node = workflow.Steps[0];
        state.Request.Baseline = PlanningContext.Clone(state.Graph);
        var prior = state.Request.Baseline.Workflows[0].Steps[0];
        prior.Input = Obj(("flag", new() { Kind = "boolean", Boolean = false }));
        prior.OutputSchema = new() { Type = "object", Properties = [new() { Name = "flag", Required = true, Schema = new() { Type = "boolean" } }] };
        var hole = new PlanningHole { Path = "/workflows/0/steps/0/input", ExpectedSchema = PlanningGraphCompiler.ToJsonSchema(prior.OutputSchema, state.Preparation!) };
        Assert.NotNull(PlanningBaselineValues.ResultSchema(state, workflow, node));
        Assert.False(PlanningBaselineValues.Literal(state, workflow, node, hole)!.Members[0].Value.Boolean);
        prior.OutputSchema = new() { CapabilityId = "old_contract", SchemaPointer = "/output" };
        Assert.Equal("boolean", PlanningBaselineValues.ResultSchema(state, workflow, node)!.Properties.Single().Schema.Type);
        hole.ExpectedSchema["properties"]!["flag"]!["type"] = "number";
        Assert.Null(PlanningBaselineValues.Literal(state, workflow, node, hole));
        node.Purpose += " revised";
        Assert.Null(PlanningBaselineValues.ResultSchema(state, workflow, node));
        node.Type = "mcp.call";
        Assert.Null(PlanningBaselineValues.PureProducer(state, workflow, node));
    }

    [Fact]
    public void ReviewedPortRevisionReopensOnlyItsBaselineContract()
    {
        var state = Session(PlanningStatus.Generating); state.Preparation = Preparation(); state.BehaviorPlan = BehaviorPlan();
        state.BehaviorPlan.Workflows[0].Inputs = [new("source", "Required structured source", true), new("retained", "Retained number", true)];
        state.Request.Baseline = Graph();
        state.Request.Baseline.Workflows[0].Inputs = [new() { Name = "source", Schema = new() { Type = "string" } }, new() { Name = "retained", Schema = new() { Type = "number" } }];
        state.BehaviorRevision = new() { Located = true, Fields = [new("/workflows/0/inputs/0/description", "/workflows/@main/inputs/@source/description", "replace", "old", "Required structured source")] };
        PlanningGraphSkeleton.Create(state);
        Assert.Equal("unresolved", state.Graph!.Workflows[0].Inputs[0].Schema.Type);
        Assert.Equal("number", state.Graph.Workflows[0].Inputs[1].Schema.Type);
        Assert.Single(state.Construction.Holes, h => h.Path.StartsWith("/workflows/0/inputs/", StringComparison.Ordinal));
        Assert.Equal("string", state.Request.Baseline.Workflows[0].Inputs[0].Schema.Type);
    }

    private static PlanningSnapshot Ready()
    {
        var state = Session(PlanningStatus.Generating); state.Preparation = Preparation(); state.BehaviorPlan = BehaviorPlan();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        PlanningGraphSkeleton.Create(state); PlanningDataflowResolver.Resolve(state); return state;
    }
    [Fact]
    public async Task FieldsConvergeWithoutModelAuthoredWorkflowAndExecuteAfterApproval()
    {
        var state = Ready(); var topology = state.Construction.SkeletonFingerprint; var runtime = new FakeRuntime(); var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 20 && !PlanningStatus.IsWaiting(state.Status); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ":" + d.Message)));
        Assert.Equal(topology, PlanningGraphSkeleton.Fingerprint(state.Graph!));
        Assert.All(state.Construction.Holes, h => Assert.True(h.Resolved));
        Assert.All(runtime.Requests.Where(r => r.ClientRequestId!.Contains(":construction:", StringComparison.Ordinal)), r =>
            Assert.Equal(new[] { "assignments" }, r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key)));
        state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Approved, state.Status);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("Hello", result.Outputs!["message"]!.GetValue<string>());
    }
    [Fact]
    public void UnresolvedFieldsCannotBeLoweredAndKnownTopologyCannotBePatched()
    {
        var state = Ready();
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(state.Graph!, state.Preparation!));
        Assert.Empty(PlanningPatches.Scope(state.Graph!, [new("BAD", "/workflows/0/steps/0/type", "An immutable executor")]));
        Assert.Empty(PlanningPatches.Scope(state.Graph!, [new("BAD", "/workflows/0/inputs", "No whole collection") ]));
    }
    [Fact]
    public void MessageChangesDoNotCountAsProgressAndAllowancesAreIndependent()
    {
        var a = new PlanningDiagnostic("MISSING", "/workflows/0/outputs/0/value", "first wording");
        Assert.False(PlanningTypedRepair.IsProgress(new(1, [a], []), new(1, [a with { Message = "new wording" }], [])));
        var state = Ready(); state.Request.MaxRepairsPerWorkflowGate = 5;
        for (var i = 0; i < 5; i++) PlanningRepairAllowances.Reserved(state, "main", PlanningGates.Typed);
        Assert.False(PlanningRepairAllowances.Available(state, "main", PlanningGates.Typed));
        Assert.True(PlanningRepairAllowances.Available(state, "main", PlanningGates.Compilation));
        Assert.True(PlanningRepairAllowances.Available(state, "child", PlanningGates.Typed));
    }
}
