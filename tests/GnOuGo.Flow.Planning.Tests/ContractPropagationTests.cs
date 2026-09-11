using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ContractPropagationTests
{
    private static PlanningSchema Unknown() => new() { Type = PlanningGraphSkeleton.Unresolved };
    private static PlanningSchema Object(params (string Name, PlanningSchema Schema)[] fields) => new()
    { Type = "object", Properties = fields.Select(f => new PlanningPort { Name = f.Name, Required = true, Schema = f.Schema }).ToList() };

    [Fact]
    public void IndependentNestedRequirementsMergeWithoutDroppingKnownMembers()
    {
        var prior = Object(("locked", new() { Type = "string", Enum = ["kept"] }), ("nested", Object(("a", Unknown()), ("b", Unknown()))));
        prior.Properties[0].Default = Str("kept"); prior.AdditionalProperties = new() { Type = "boolean", Nullable = true };
        var result = PlanningSchemaRefinement.Resolve(prior,
            [new(new() { Type = "number" }, ["nested", "a"]), new(new() { Type = "boolean" }, ["nested", "b"])], Preparation());
        Assert.Equal("kept", result.Properties[0].Schema.Enum.Single());
        Assert.Equal("kept", result.Properties[0].Default!.Text); Assert.True(result.AdditionalProperties!.Nullable);
        Assert.Equal("number", result.Properties[1].Schema.Properties[0].Schema.Type);
        Assert.Equal("boolean", result.Properties[1].Schema.Properties[1].Schema.Type);
        Assert.Equal("unresolved", prior.Properties[1].Schema.Properties[0].Schema.Type);
    }

    [Fact]
    public void ConflictingConsumersAndChangesToLockedFragmentsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PlanningSchemaRefinement.Resolve(Unknown(),
            [new(new() { Type = "number" }, ["field"]), new(new() { Type = "string" }, ["field"])], Preparation()));
        var prior = Object(("known", new() { Type = "number", Nullable = true }), ("pending", Unknown()));
        Assert.Throws<InvalidOperationException>(() => PlanningSchemaRefinement.Resolve(prior,
            [new(new() { Type = "number" }, ["known"])], Preparation()));
    }

    [Fact]
    public void BackwardPropagationEstablishesLocalResultAndInputContractsToAFixedPoint()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        workflow.Inputs = [new() { Name = "source", Required = true, Schema = Unknown() }];
        workflow.Steps[0].Input = Obj(("value", new() { Kind = "input", Source = "source" }));
        workflow.Outputs[0].Schema = Object(("value", new() { Type = "number" }));
        workflow.Outputs[0].Value = new() { Kind = "output", Source = workflow.Steps[0].Key };
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/inputs/0/schema", "schema", "source");
        PlanningGraphSkeleton.Add(state, workflow, workflow.Steps[0], "/workflows/0/steps/0/outputSchema", "schema", "result");
        var iterations = 0;
        while (PlanningContractPropagation.Resolve(state)) Assert.True(++iterations < 5);
        Assert.Equal("number", state.Graph.Workflows[0].Inputs[0].Schema.Type);
        Assert.Equal("number", state.Graph.Workflows[0].Steps[0].OutputSchema!.Properties[0].Schema.Type);
        Assert.All(state.Construction.Holes, h => { Assert.True(h.Resolved); Assert.Equal("deterministic", h.ResolutionOrigin); });
    }

    [Fact]
    public void CalleeContractsPropagateBackwardAndCallerValuesPropagateForward()
    {
        var state = HoleSessionTests.Ready(); var parent = state.Graph!.Workflows[0];
        parent.Inputs = [new() { Name = "source", Required = true, Schema = Unknown() }];
        parent.Outputs.Clear();
        parent.Steps = [new() { Key = "call", Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = "child" }), ("args", Obj(("arg", new() { Kind = "input", Source = "source" })))) }];
        var child = new PlanningWorkflow { Key = "child", Inputs = [new() { Name = "arg", Required = true, Schema = Object(("items", new() { Type = "array", Items = new() { Type = "integer" } })) }] };
        state.Graph.Workflows.Add(child); state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, parent, null, "/workflows/0/inputs/0/schema", "schema", "source");
        Assert.True(PlanningContractPropagation.Resolve(state));
        Assert.Equal("integer", state.Graph.Workflows[0].Inputs[0].Schema.Properties.Single().Schema.Items!.Type);
        state.Graph.Workflows[1].Inputs[0].Schema = Unknown(); state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, state.Graph.Workflows[1], null, "/workflows/1/inputs/0/schema", "schema", "callee");
        Assert.True(PlanningContractPropagation.Resolve(state));
        Assert.Equal("integer", state.Graph.Workflows[1].Inputs[0].Schema.Properties.Single().Schema.Items!.Type);
    }

    [Fact]
    public void ConflictsAreLocatedAndAtomicBeforeAnyDispatch()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        workflow.Inputs = [new() { Name = "source", Schema = Unknown() }]; workflow.Steps.Clear();
        workflow.Outputs = [new() { Name = "a", Schema = new() { Type = "string" }, Value = new() { Kind = "input", Source = "source" } },
            new() { Name = "b", Schema = new() { Type = "number" }, Value = new() { Kind = "input", Source = "source" } }];
        state.Construction.Holes.Clear(); PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/inputs/0/schema", "schema", "source");
        var before = PlanningGraphCompiler.Fingerprint(state.Graph);
        var error = Assert.Throws<PlanningHoleUnavailableException>(() => PlanningContractPropagation.Resolve(state));
        Assert.Contains("@source", error.Location); Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Graph));
        Assert.Empty(state.Construction.PendingCalls);
    }

    [Fact]
    public void ConsumerContractCannotEstablishAnOpaqueExternalProducer()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0]; var node = workflow.Steps[0];
        node.Type = "mcp.call"; node.CapabilityId = "opaque";
        state.Preparation!.Capabilities.Add(new() { Id = "opaque", StepType = "mcp.call", OutputSchema = new() { ["type"] = "object" } });
        workflow.Outputs[0].Schema = Object(("value", new() { Type = "string" }));
        workflow.Outputs[0].Value = new() { Kind = "output", Source = node.Key };
        state.Construction.Holes.Clear(); PlanningGraphSkeleton.Add(state, workflow, node, "/workflows/0/steps/0/outputSchema", "schema", "opaque");
        Assert.False(PlanningContractPropagation.Resolve(state)); Assert.False(state.Construction.Holes.Single().Resolved);
        Assert.Single(state.Preparation.Capabilities.Single().OutputSchema);
    }

    [Fact]
    public void ScalarPublicOutputDoesNotTurnASetResultIntoAScalar()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        workflow.Outputs[0].Schema = new() { Type = "string" };
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, workflow.Steps[0], "/workflows/0/steps/0/outputSchema", "schema", "producer result");
        Assert.False(PlanningContractPropagation.Resolve(state));
        Assert.Equal(PlanningGraphSkeleton.Unresolved, workflow.Steps[0].OutputSchema!.Type);
    }

    [Fact]
    public void PartialSchemaContainersBecomeExactMemberHoles()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        workflow.Steps[0].OutputSchema = Object(("locked", new() { Type = "string", Enum = ["a"] }), ("pending", new() { Type = "array", Items = Unknown() }));
        state.Construction.Holes.Clear(); PlanningGraphSkeleton.Add(state, workflow, workflow.Steps[0], "/workflows/0/steps/0/outputSchema", "schema", "result");
        Assert.True(PlanningSchemaPropagation.Resolve(state, workflow));
        var hole = Assert.Single(state.Construction.Holes, h => !h.Resolved);
        Assert.EndsWith("/properties/1/schema/items", hole.Path);
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        Assert.DoesNotContain("locked", request.Schema.ToJsonString());
        Assert.DoesNotContain("locked", request.Prompt);
        PlanningConvergence.Refresh(state);
        Assert.Equal(1, state.Construction.Workflows.Single().TotalHoles);
    }
}
