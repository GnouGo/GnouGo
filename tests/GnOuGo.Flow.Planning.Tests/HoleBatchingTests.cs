using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class HoleBatchingTests
{
    [Fact]
    public void LoopCollectionsWaitForTheExternalPrerequisitesOfTheirBody()
    {
        var (state, workflow, producerHole) = ConvergenceDomainTests.Input();
        workflow.Steps[0].OperationIds = ["producer_operation"];
        var loop = new PlanningNode { Key = "iteration", Type = "loop.sequential", OperationIds = ["consume_operation"],
            Input = Obj(("items", new() { Kind = "unresolved" })), Steps = [new() { Key = "body", Type = "mcp.call", CapabilityId = "body_cap", OperationIds = ["consume_operation"], Input = Obj() }] };
        workflow.Steps.Add(loop);
        state.Preparation!.Capabilities.Add(new() { Id = "body_cap", StepType = "mcp.call", OperationIds = ["consume_operation"], InputOperationIds = ["producer_operation"] });
        PlanningGraphSkeleton.Add(state, workflow, loop, "/workflows/0/steps/1/input/members/0/value", "value", "Iterate the declared producer results", new() { ["type"] = "array" });
        var loopHole = state.Construction.Holes.Single(h => h.NodeKey == loop.Key);
        Assert.Contains(producerHole.Id, PlanningWorkflowConstruction.HoleDependencies(state, workflow)[loopHole.Id]);
        var request = PlanningWorkflowConstruction.Batch(state, workflow);
        Assert.DoesNotContain(request.Holes, h => h.Id == loopHole.Id);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.Request.Schema, true));
        Assert.False(PlanningHoleEligibility.Analyze(state, workflow, loopHole).Literal);
    }
    [Fact]
    public void IndependentArtifactArgumentsWithGuaranteedSharedCoverageBatchTogether()
    {
        var (state, workflow, first) = ConvergenceDomainTests.Input(); var consumer = workflow.Steps[0];
        var second = AddField(state, workflow, false);
        var consume = state.Preparation!.Capabilities.Single();
        consume.InputOperationIds = ["produce"];
        consume.ArtifactContract = new(1, [], [new("first", "/argument", true), new("second", "/second", true)]);
        var producer = new PlanningNode { Key = "producer", Type = "mcp.call", CapabilityId = "producer", OperationIds = ["produce"], Input = Obj() };
        workflow.Steps.Insert(0, producer);
        foreach (var hole in state.Construction.Holes) hole.Path = hole.Path.Replace("/steps/0/", "/steps/1/", StringComparison.Ordinal);
        state.Construction.Dataflow!.InputObligations.Clear();
        state.Preparation.Capabilities.Add(new() { Id = "producer", StepType = "mcp.call", OperationIds = ["produce"],
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"},"c":{"type":"string"},"d":{"type":"string"}},"required":["a","b","c","d"],"additionalProperties":false}""")!.AsObject(),
            ArtifactContract = new(1, [new("first", "/a", "reference"), new("first", "/b", "reference"), new("second", "/c", "reference"), new("second", "/d", "reference")], []) });
        Assert.All(new[] { first, second }, h => { Assert.Equal(2, PlanningHoleEligibility.Analyze(state, workflow, h).Direct.Count); Assert.NotEmpty(PlanningHoleEligibility.Analyze(state, workflow, h).Outstanding); });
        // The baseline's outstanding-obligation Take(1) needed two requests. Either
        // choice proves the shared producer, so neither changes its neighbor's domain.
        Assert.Equal(2, PlanningWorkflowConstruction.Batch(state, workflow).Holes.Length);
    }

    [Fact]
    public void ResultSchemaWaitsForItsDeclaredBusinessInputContract()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        workflow.Inputs = [new() { Name = "source", Required = true, Schema = new() { Type = "unresolved" } }];
        state.Construction.Dataflow!.InputObligations[workflow.Key + "/" + workflow.Steps[0].Key] = ["source"];
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/inputs/0/schema", "schema", "Source contract");
        Assert.Equal("/workflows/0/inputs/0/schema", Assert.Single(PlanningWorkflowConstruction.Batch(state, workflow).Holes).Path);
    }

    [Fact]
    public void CyclicOperationPrerequisitesStopBeforeAnyReservation()
    {
        var (state, workflow, _) = ConvergenceDomainTests.Input();
        var second = AddField(state, workflow, true);
        workflow.Steps[0].OperationIds = ["first"];
        workflow.Steps[1].OperationIds = ["second"]; workflow.Steps[1].CapabilityId = "other";
        state.Preparation!.Capabilities.Single().InputOperationIds = ["second"];
        state.Preparation.Capabilities.Add(new() { Id = "other", StepType = "mcp.call", InputOperationIds = ["first"] });
        var before = PlanningGraphCompiler.Fingerprint(state.Graph!);
        var error = Assert.Throws<PlanningHoleUnavailableException>(() => PlanningWorkflowConstruction.Batch(state, workflow));
        Assert.Contains("cycle", error.Message);
        Assert.StartsWith("/workflows/@", error.Location);
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Empty(state.Construction.PendingCalls);
        Assert.Empty(state.RequestAccounting);
    }
    [Fact]
    public void EstablishedSchemaEqualityRequiresOnlyOneSchemaAssignment()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        workflow.Inputs = [new() { Name = "source", Required = true, Schema = new() { Type = "unresolved" } }];
        workflow.Steps.Clear(); workflow.Outputs[0].Value = new() { Kind = "input", Source = "source" };
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/inputs/0/schema", "schema", "Business input");
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/outputs/0/schema", "schema", "Returned contract");
        var batch = PlanningWorkflowConstruction.Batch(state, workflow);
        var source = Assert.Single(batch.Holes); Assert.Contains("/inputs/", source.Path);
        PlanningBindingResolution.Assign(state, source, System.Text.Json.JsonSerializer.SerializeToNode(new PlanningSchema { Type = "integer" }, PlanningJsonContext.Default.PlanningSchema));
        Assert.True(PlanningContractPropagation.Resolve(state));
        Assert.All(state.Construction.Holes, h => Assert.True(h.Resolved));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentFieldsShareOneSmallerRequest(bool separateNodes)
    {
        var (state, workflow, first) = ConvergenceDomainTests.Input();
        state.Construction.Dataflow!.InputObligations.Clear();
        var second = AddField(state, workflow, separateNodes);
        var before = new[] { first, second }.Select(h => PlanningHoleRequests.Create(state, workflow, [h]))
            .Sum(r => PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.Schema));
        var batch = PlanningWorkflowConstruction.Batch(state, workflow);
        Assert.Equal(2, batch.Holes.Length); // Baseline selected only the first producer, or one coupled argument.
        Assert.True(PlanningJsonTransport.EstimateInputTokens(batch.Request.Prompt, batch.Request.Schema) < before);
        Assert.Empty(PlanningContractValidation.ValidateSchema(batch.Request.Schema, true));
        var again = PlanningWorkflowConstruction.Batch(state, workflow);
        Assert.Equal(batch.Holes.Select(h => h.Id), again.Holes.Select(h => h.Id));
        Assert.Equal(batch.Request.Prompt, again.Request.Prompt);
    }

    [Fact]
    public void SharedVariableObligationCoverageRemainsSequential()
    {
        var (state, workflow, _) = ConvergenceDomainTests.Input();
        AddField(state, workflow, false);
        Assert.Single(PlanningWorkflowConstruction.Batch(state, workflow).Holes);
    }

    [Fact]
    public void OneScalarSourceForSeveralArgumentsIsNotAForcedIdentityAssignment()
    {
        var (state, workflow, first) = ConvergenceDomainTests.Input();
        var second = AddField(state, workflow, false);
        var topology = PlanningGraphSkeleton.Fingerprint(state.Graph!);
        // The live regression copied the entire locator into two scalar components.
        // Both had one compatible source, but dependency coverage was shared and
        // literal/computation alternatives still represented semantic choices.
        PlanningBindingResolution.Resolve(state, workflow);
        Assert.All(new[] { first, second }, h => Assert.False(h.Resolved));
        var actual = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            workflow = state.Graph!.Workflows[0];
            var batch = PlanningWorkflowConstruction.Batch(state, workflow);
            var hole = Assert.Single(batch.Holes);
            var parameter = Assert.Single(batch.Request.ParameterScopes[hole.Id]);
            var value = PlanningHoleAssignments.Value(new JsonObject { ["kind"] = "compute", ["expression"] = parameter + ".split(':')[" + index + "]" },
                batch.Request.Bindings, batch.Request.ParameterScopes[hole.Id])!;
            Assert.Null(PlanningHoleEligibility.Validate(state, workflow, hole, value));
            actual.Add(new GnOuGo.Flow.Core.Expressions.ExpressionEvaluator().Evaluate(value.Text!, new JsonObject { [parameter] = "first:second" })!.GetValue<string>());
            PlanningBindingResolution.Assign(state, hole, System.Text.Json.JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.PlanningValue));
            PlanningBindingResolution.Resolve(state, state.Graph.Workflows[0]);
        }
        Assert.Equal(new[] { "first", "second" }, actual);
        Assert.Equal(topology, PlanningGraphSkeleton.Fingerprint(state.Graph!));
        Assert.Empty(state.RepairAllowances);
    }

    [Fact]
    public void ProducersAndUnresolvedInputDefaultsPrecedeConsumers()
    {
        var (state, workflow, producer) = ConvergenceDomainTests.Input();
        var consumer = AddField(state, workflow, true);
        workflow.Steps[0].OutputSchema = new() { Type = "string" };
        state.Construction.Dataflow!.InputObligations[workflow.Key + "/" + consumer.NodeKey] = ["source"];
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/inputs/0/default", "default", "Declared default");
        var inputDefault = state.Construction.Holes.Single(h => h.Kind == "default");
        var graph = PlanningWorkflowConstruction.HoleDependencies(state, workflow);
        Assert.Contains(producer.Id, graph[consumer.Id]);
        Assert.Contains(inputDefault.Id, graph[producer.Id]);
        Assert.Equal(inputDefault.Id, Assert.Single(PlanningWorkflowConstruction.Batch(state, workflow).Holes).Id);
    }

    [Fact]
    public void BatchesRespectTheCompleteRequestCeiling()
    {
        var (state, workflow, first) = ConvergenceDomainTests.Input();
        state.Construction.Dataflow!.InputObligations.Clear();
        var second = AddField(state, workflow, true);
        first.Purpose = new string('x', 4000); second.Purpose = new string('y', 4000);
        var individual = PlanningHoleRequests.Create(state, workflow, [first]);
        state.Request.Generation.MaxInputTokensPerRequest = PlanningJsonTransport.EstimateInputTokens(individual.Prompt, individual.Schema) + 10;
        var batch = PlanningWorkflowConstruction.Batch(state, workflow);
        Assert.Single(batch.Holes);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(batch.Request.Prompt, batch.Request.Schema) <= state.Request.Generation.MaxInputTokensPerRequest);
    }

    [Fact]
    public void SchemaDependenciesPreventSpeculativeValueRequests()
    {
        var (state, workflow, value) = ConvergenceDomainTests.Input();
        workflow.Inputs[0].Schema = new() { Type = "unresolved" };
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/inputs/0/schema", "schema", "Business input contract");
        var schema = state.Construction.Holes.Single(h => h.Kind == "schema");
        Assert.Contains(schema.Id, PlanningWorkflowConstruction.HoleDependencies(state, workflow)[value.Id]);
        Assert.Equal(schema.Id, Assert.Single(PlanningWorkflowConstruction.Batch(state, workflow).Holes).Id);
    }

    private static PlanningHole AddField(PlanningSnapshot state, PlanningWorkflow workflow, bool separateNode)
    {
        var node = workflow.Steps[0]; var path = "/workflows/0/steps/0/input/members/0/value/members/1/value";
        if (separateNode)
        {
            node = new() { Key = "independent", Type = "mcp.call", CapabilityId = "consume", Input = Obj(("request", Obj(("argument", new() { Kind = "unresolved" })))) };
            workflow.Steps.Add(node); path = "/workflows/0/steps/1/input/members/0/value/members/0/value";
        }
        else
        {
            node.Input.Members[0].Value.Members.Add(new("second", new() { Kind = "unresolved" }));
            state.Preparation!.Capabilities.Single().InputSchema["properties"]!["second"] = new JsonObject { ["type"] = "string" };
        }
        PlanningGraphSkeleton.Add(state, workflow, node, path, "value", "Independent declared business value");
        return state.Construction.Holes.Single(h => h.Path == path);
    }
}
