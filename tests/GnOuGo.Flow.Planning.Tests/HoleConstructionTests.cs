using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class HoleConstructionTests
{
    [Fact]
    public void SchemaContextDoesNotRepeatTypedParentAndDescendantContracts()
    {
        var (state, workflow, _) = ConvergenceDomainTests.Input();
        var contract = JsonNode.Parse("""{"type":"object","properties":{"nested":{"type":"object","properties":{"value":{"type":"number","minimum":1,"maximum":5}},"required":["value"],"additionalProperties":false}},"required":["nested"],"additionalProperties":false}""")!.AsObject();
        state.Preparation!.Capabilities.Single().OutputSchema = contract;
        workflow.Outputs.Add(new() { Name = "observed", Schema = new() { Type = "unresolved" } });
        PlanningGraphSkeleton.Add(state, workflow, null, "/workflows/0/outputs/0/schema", "schema", "Public observation");
        var request = PlanningHoleRequests.Create(state, workflow, [state.Construction.Holes.Single(h => h.Kind == "schema")]);
        var candidates = PlanningDataflow.Index(workflow, state.Preparation, state.Graph!, PlanningDataflow.WorkflowOutputs).Values
            .Where(b => PlanningSchemaPropagation.Established(b.Schema) && b.Availability is not ("opaque" or "absent")).ToArray();
        var sources = request.Context["sourceContracts"]!.AsObject();
        Assert.True(sources.Count < candidates.Length);
        var original = new JsonObject(candidates.Select(b => new KeyValuePair<string, JsonNode?>(b.Id, new JsonObject { ["schema"] = b.Schema.DeepClone() })));
        Assert.True(PlanningJsonTransport.EstimateInputTokens(sources.ToJsonString(), new()) < PlanningJsonTransport.EstimateInputTokens(original.ToJsonString(), new()));
        var root = candidates.Single(b => b.Value.Kind == "output" && b.Value.Path.Count == 0);
        Assert.True(JsonNode.DeepEquals(contract, sources[root.Id]!["schema"]));
    }
    [Fact]
    public void ResultSchemaCannotInventDefaultsThatRuntimeDoesNotApply()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0];
        var node = workflow.Steps[0]; node.Type = "set";
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, node, "/workflows/0/steps/0/outputSchema", "schema", "Computed result");
        var hole = state.Construction.Holes.Single();
        var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        Assert.Equal("null", request.Schema["$defs"]!["port"]!["properties"]!["default"]!["type"]!.ToString());
        Assert.False(request.Schema["$defs"]!.AsObject().ContainsKey("value"));
        var contract = new PlanningSchema { Type = "object", Properties = [new() { Name = "result", Schema = new() { Type = "string" }, Default = Str("invented") }] };
        JsonObject Response() => new() { ["assignments"] = new JsonObject { [hole.Id] = PlanningModelValues.Compact(System.Text.Json.JsonSerializer.SerializeToNode(contract, PlanningJsonContext.Default.PlanningSchema)) } };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(), request.Schema));
        contract.Properties[0].Default = null;
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(), request.Schema));
        var previous = request.Schema.DeepClone().AsObject();
        var definitions = PlanningSchemas.ValueDefinitions();
        previous["$defs"]!["port"]!["properties"]!["default"] = definitions["port"]!["properties"]!["default"]!.DeepClone();
        previous["$defs"]!["value"] = definitions["value"]!.DeepClone();
        previous["$defs"]!["member"] = definitions["member"]!.DeepClone();
        Assert.True(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.Schema) < PlanningJsonTransport.EstimateInputTokens(request.Prompt, previous));
    }
    [Fact]
    public void ProducerSchemaRequestsIncludeOnlyUnresolvedConsumerFields()
    {
        var (state, workflow, consumerHole) = ConvergenceDomainTests.Input();
        state.Preparation!.Capabilities.Single().InputOperationIds = ["produce"];
        state.Preparation.Capabilities.Single().InputSchema["properties"]!["unrelatedOptional"] = new JsonObject { ["type"] = "string", ["description"] = new string('x', 5000) };
        var producer = new PlanningNode { Key = "producer", Type = "set", OperationIds = ["produce"], OutputSchema = new() { Type = "unresolved" } };
        workflow.Steps.Insert(0, producer);
        consumerHole.Path = consumerHole.Path.Replace("/steps/0/", "/steps/1/", StringComparison.Ordinal);
        PlanningGraphSkeleton.Add(state, workflow, producer, "/workflows/0/steps/0/outputSchema", "schema", "Declared producer result");
        var request = PlanningHoleRequests.Create(state, workflow, [state.Construction.Holes.Single(h => h.Kind == "schema")]);
        Assert.DoesNotContain("unrelatedOptional", request.Prompt);
        var expected = PlanningHoleRequests.Expected(state, workflow, consumerHole);
        Assert.True(JsonNode.DeepEquals(expected, request.Context["consumerContracts"]!["consume"]!["unresolvedInputs"]![consumerHole.CanonicalLocation]));
        var previous = request.Prompt + state.Preparation.Capabilities.Single().InputSchema.ToJsonString();
        Assert.True(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.Schema) < PlanningJsonTransport.EstimateInputTokens(previous, request.Schema));
        Assert.True(state.Preparation.Capabilities.Single().InputSchema["properties"]!.AsObject().ContainsKey("unrelatedOptional"));
    }
    [Fact]
    public void ProducerSchemaRequestsCannotSatisfyOriginalArtifactArguments()
    {
        var (state, workflow, consumerHole) = ConvergenceDomainTests.Input();
        var capability = state.Preparation!.Capabilities.Single();
        capability.InputOperationIds = ["produce"];
        capability.InputSchema["properties"]!["argument"]!["description"] = new string('x', 4000);
        var producer = new PlanningNode { Key = "producer", Type = "set", OperationIds = ["produce"], OutputSchema = new() { Type = "unresolved" } };
        workflow.Steps.Insert(0, producer);
        consumerHole.Path = consumerHole.Path.Replace("/steps/0/", "/steps/1/", StringComparison.Ordinal);
        PlanningGraphSkeleton.Add(state, workflow, producer, "/workflows/0/steps/0/outputSchema", "schema", "Declared producer result");
        var holes = state.Construction.Holes.Where(h => h.Kind == "schema").ToArray();
        var before = PlanningHoleRequests.Create(state, workflow, holes);
        capability.ArtifactContract = new(1, [], [new("original.document", "/argument", true)]);
        var after = PlanningHoleRequests.Create(state, workflow, holes);
        Assert.Null(after.Context["consumerContracts"]);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(after.Prompt, after.Schema) < PlanningJsonTransport.EstimateInputTokens(before.Prompt, before.Schema));
        Assert.Single(capability.ArtifactContract.Consumes);
        Assert.Equal(4000, capability.InputSchema["properties"]!["argument"]!["description"]!.ToString().Length);
    }
    [Fact]
    public void StructuredResultResponseForbidsOptionalMembersBeforeDispatch()
    {
        var state = HoleSessionTests.Ready(); var workflow = state.Graph!.Workflows[0]; var node = workflow.Steps[0];
        node.Type = "mcp.call"; node.CapabilityId = "external"; node.StructuredOutput = new(new() { Type = "unresolved" });
        state.Preparation!.Capabilities.Add(new() { Id = "external", StepType = "mcp.call" });
        state.Construction.Holes.Clear();
        PlanningGraphSkeleton.Add(state, workflow, node, "/workflows/0/steps/0/structuredOutput/schema", "schema", "Extract the declared result");
        var hole = state.Construction.Holes.Single(); var request = PlanningHoleRequests.Create(state, workflow, [hole]);
        var contract = new PlanningSchema { Type = "object", Properties = [new() { Name = "body", Required = false, Schema = new() { Type = "string", Nullable = true } }] };
        JsonObject Response() => new() { ["assignments"] = new JsonObject { [hole.Id] = PlanningModelValues.Compact(System.Text.Json.JsonSerializer.SerializeToNode(contract, PlanningJsonContext.Default.PlanningSchema)) } };
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.Schema, true));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Response(), request.Schema));
        contract.Properties[0].Required = true;
        Assert.Empty(PlanningContractValidation.ValidateInstance(Response(), request.Schema));
        Assert.DoesNotContain("/output", request.Schema.ToJsonString()); // Opaque references grant no selectable schema.
    }
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task IndivisibleTruncationStopsWithoutRetryOrBudgetReset(int limit)
    {
        var state = Ready(); state.Request.MaxRepairsPerWorkflowGate = limit;
        var graph = PlanningGraphCompiler.Fingerprint(state.Graph!);
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        state = await HoleSessionTests.Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "DECISION_OUTPUT_LIMIT");
        state = await HoleSessionTests.Advance(PlanningContext.Clone(state), runtime);
        Assert.Single(runtime.Requests); Assert.Empty(state.RepairAllowances);
        Assert.Equal(graph, PlanningGraphCompiler.Fingerprint(state.Graph!));
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
