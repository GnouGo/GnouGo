using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OptionalArgumentTests
{
    [Fact]
    public void LockedNullObjectDoesNotCreateSpeculativeMemberHoles()
    {
        var (state, workflow, _) = ConvergenceDomainTests.Input();
        var capability = state.Preparation!.Capabilities.Single();
        capability.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"context":{"type":["object","null"],"properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}},"required":["context"],"additionalProperties":false}""")!.AsObject();
        capability.RequestBindings = [new("/context", null)];
        state.Construction.Holes.Clear();
        PlanningSkeletonInputs.Build(state, workflow, workflow.Steps[0], "/workflows/0/steps/0", capability);
        Assert.Empty(state.Construction.Holes); // Previously expanded an immutable null into a member requiring model work.
        Assert.Equal("null", PlanningGraphValidation.Member(PlanningGraphValidation.Member(workflow.Steps[0].Input, "request")!, "context")!.Kind);
    }

    [Fact]
    public void SkeletonPreservesAnOptionalArgumentNeededForDynamicBusinessInput()
    {
        var (state, workflow, _) = ConvergenceDomainTests.Input();
        var capability = state.Preparation!.Capabilities.Single();
        capability.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"mode":{"const":"inspect","type":"string"},"context":{"type":"string"}},"required":["mode"],"additionalProperties":false}""")!.AsObject();
        state.Construction.Holes.Clear();
        PlanningSkeletonInputs.Build(state, workflow, workflow.Steps[0], "/workflows/0/steps/0", capability);
        var optional = state.Construction.Holes.Single(h => h.Optional);
        Assert.EndsWith("/value", optional.Path);
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.True(optional.Resolved);
        Assert.Equal("deterministic", optional.ResolutionOrigin);
        var request = PlanningGraphValidation.Member(state.Graph!.Workflows[0].Steps[0].Input, "request")!;
        Assert.Equal("source", PlanningGraphValidation.Member(request, "context")!.Source);
        Assert.Empty(state.Construction.PendingCalls);
    }

    [Fact]
    public void OptionalOmissionHasAnExactCoordinateAndCannotDropTheLastObligation()
    {
        var (state, workflow, hole) = ConvergenceDomainTests.Input();
        hole.Optional = true;
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.False(domain.Omission);
        Assert.NotNull(PlanningHoleEligibility.Validate(state, workflow, hole, new() { Kind = PlanningSkeletonInputs.Omitted }));
        state.Construction.Dataflow!.InputObligations.Clear();
        domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        Assert.True(domain.Omission);
        Assert.Null(PlanningBindingResolution.Unique(state, workflow, hole));
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(new JsonObject { ["assignments"] = new JsonObject
        { [hole.Id] = new JsonObject { ["kind"] = "absent" } } }, request.Schema));
        Assert.Null(PlanningHoleEligibility.Validate(state, workflow, hole, new() { Kind = PlanningSkeletonInputs.Omitted }));
    }

    [Fact]
    public void DefaultsCannotChooseWhichOptionalArgumentCarriesTheBusinessObligation()
    {
        var (state, workflow, _) = ConvergenceDomainTests.Input();
        var capability = state.Preparation!.Capabilities.Single();
        capability.InputSchema = JsonNode.Parse("""{"type":"object","properties":{"context":{"type":["string","null"],"default":null},"other":{"type":["string","null"],"default":null}},"required":[],"additionalProperties":false}""")!.AsObject();
        state.Construction.Holes.Clear();
        PlanningSkeletonInputs.Build(state, workflow, workflow.Steps[0], "/workflows/0/steps/0", capability);
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.All(state.Construction.Holes, h => Assert.False(h.Resolved));
        var first = state.Construction.Holes[0];
        PlanningBindingResolution.Assign(state, first, System.Text.Json.JsonSerializer.SerializeToNode(new PlanningValue { Kind = "input", Source = "source" }, PlanningJsonContext.Default.PlanningValue));
        PlanningBindingResolution.Resolve(state, state.Graph!.Workflows[0]);
        Assert.False(state.Construction.Holes[1].Resolved); // A default cannot silently replace still-eligible business data.
        state.Construction.Dataflow!.InputObligations.Clear();
        PlanningBindingResolution.Resolve(state, state.Graph.Workflows[0]);
        Assert.All(state.Construction.Holes, h => Assert.True(h.Resolved));
        Assert.Equal("null", PlanningGraphValidation.Member(PlanningGraphValidation.Member(state.Graph.Workflows[0].Steps[0].Input, "request")!, "other")!.Kind);
    }

    [Fact]
    public void OmissionKeepsMemberCoordinatesAndIsDistinctFromNullInYaml()
    {
        var graph = Graph(); var preparation = Preparation(); var node = graph.Workflows[0].Steps[0];
        node.Input = Obj(("absent", new() { Kind = PlanningSkeletonInputs.Omitted }), ("message", Str("Hello")), ("presentNull", new() { Kind = "null" }));
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        Assert.DoesNotContain("absent:", yaml);
        Assert.Contains("presentNull: null", yaml);
        Assert.Equal("message", node.Input.Members[1].Name);
        Assert.Empty(PlanningGraphCompiler.ValidateValues(graph, preparation));
        node.Input = new() { Kind = PlanningSkeletonInputs.Omitted };
        Assert.NotEmpty(PlanningGraphCompiler.ValidateValues(graph, preparation));
    }

    [Fact]
    public void RequiredArgumentCannotBeOmittedEvenInAManuallyAlteredGraph()
    {
        var (state, workflow, _) = ConvergenceDomainTests.Input();
        PlanningGraphValidation.Member(workflow.Steps[0].Input, "request")!.Members[0] = new("argument", new() { Kind = PlanningSkeletonInputs.Omitted });
        Assert.Contains(PlanningGraphValidation.Validate(state.Graph!, state.Preparation!), d => d.Code == "CAPABILITY_ARGUMENT_MISSING");
    }
}
