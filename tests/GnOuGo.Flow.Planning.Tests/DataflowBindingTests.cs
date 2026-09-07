using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DataflowBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyApprovalResolvesInputObligations_WithBoundedEvidenceValidation(bool invented)
    {
        var state = ConstructionUnitTests.ApprovedSkeleton();
        state.Graph!.Workflows[0].Inputs.Add(new() { Name = "source", Schema = new() { Type = "string" } });
        state.BehaviorPlan!.Workflows[0].Inputs.Add(new("source", "Dynamic message", true));
        state.BehaviorPlan.Workflows[0].Steps[0].InputDependencies = null;
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan); var approval = state.ApprovedBehaviorHash;
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            Assert.Equal("dataflow", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["greeting"] = new JsonArray(new JsonObject
                { ["input"] = "source", ["inputExcerpt"] = invented ? "invented" : "Dynamic message", ["operationExcerpt"] = "Return a greeting" }) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(approval, state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash); Assert.NotNull(state.Dataflow);
        Assert.Equal(invented ? 2 : 1, state.Dataflow.AssessmentCalls);
        if (invented) { Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Contains(state.Diagnostics, d => d.Code == "DATAFLOW_EVIDENCE_INVALID"); Assert.Null(state.Outcome); }
        else Assert.Equal(new[] { "source" }, state.Dataflow.InputObligations["main/greeting"]);
    }

    private static (PlanningGraph Graph, PlanningPreparation Preparation) Fixture()
    {
        var graph = Graph(); var prep = Preparation();
        graph.Workflows[0].Inputs.Add(new() { Name = "resource", Schema = new() { Type = "string" } });
        graph.Workflows[0].Steps.Insert(0, new() { Key = "read", Type = "mcp.call", CapabilityId = "reader", Input = Obj(("request", Obj(("resource", new() { Kind = "input", Source = "resource" })))) });
        prep.Capabilities.Add(new() { Id = "reader", StepType = "mcp.call", Server = "renamed", Method = "inspect", Kind = "tool", OutputSchema = new(),
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"resource":{"type":"string"},"optional":{"type":["string","null"]}},"required":["resource"],"additionalProperties":false}""")!.AsObject() });
        return (graph, prep);
    }

    [Fact]
    public void OpaqueResultsExposeOnlyWholeResultBindings_AndUnknownReferencesFailClosed()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        var index = PlanningDataflow.Index(workflow, prep, graph, "greeting");
        var opaque = Assert.Single(index.Values, b => b.Value.Source == "read");
        Assert.Equal("opaque", opaque.Availability); Assert.Empty(opaque.Value.Path);
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"], ContractVersion = 2 };
        var schema = PlanningConstruction.Schema(workflow, unit, prep, graph);
        var candidate = PlanningConstruction.Values(workflow, unit);
        candidate["nodes"]!["greeting"]!["input"] = new JsonObject { ["kind"] = "binding", ["reference"] = "invented" };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        Assert.Throws<InvalidOperationException>(() => PlanningConstruction.Apply(graph, unit, candidate, prep));
    }

    [Fact]
    public void ConditionalChildIsUnavailableOutsideItsBranch()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var read = workflow.Steps[0];
        workflow.Steps[0] = new() { Key = "choose", Type = "switch", Expr = Str("take"), Cases = [new("take", null, [read])], Default = [] };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
        read.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "known", Schema = new() { Type = "string" } }] });
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
    }

    [Fact]
    public async Task IncompleteModelOutputIsReportedAsACompletionLimit_NotMalformedIntent()
    {
        var state = Session(); state.IntentChecked = true; state.Preparation = Preparation();
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Equal(2, runtime.Requests.Count);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT"); Assert.Null(state.ApprovedBehaviorHash);
    }

    [Fact]
    public void BehaviorContextKeepsObligationsWithoutExecutableSchemas()
    {
        var (_, prep) = Fixture(); prep.Capabilities[0].OperationIds = ["observe"];
        prep.Capabilities[0].Description = "Observe the supplied resource";
        prep.Capabilities[0].InputSchema["description"] = new string('x', 32_000);
        var context = TypedWorkflowPlanner.BehaviorCapabilities(prep);
        Assert.Contains("observe", context); Assert.Contains("Observe the supplied resource", context);
        Assert.DoesNotContain("inputSchema", context); Assert.DoesNotContain("outputSchema", context); Assert.True(context.Length < 3000);
    }

    [Fact]
    public void RequiredNativeDecisionCannotBeReplacedByAnUnboundLocalComputation()
    {
        var behavior = BehaviorPlan(); var prep = Preparation();
        prep.Capabilities.Add(new() { Id = "evaluate", StepType = "decision.evaluate", Resolution = "native", Required = true, OperationIds = ["assess"] });
        behavior.Workflows[0].OperationIds.Add("assess");
        behavior.Workflows[0].Steps.Add(new() { Key = "assess", Kind = "operation", Purpose = "Assess runtime evidence", OperationIds = ["assess"] });
        Assert.Contains(PlanningBehaviorPlans.Validate(behavior, prep), d => d.Message.Contains("evaluate", StringComparison.Ordinal));
        behavior.Workflows[0].Steps[^1].CapabilityId = "evaluate";
        Assert.DoesNotContain(PlanningBehaviorPlans.Validate(behavior, prep), d => d.Message.Contains("evaluate", StringComparison.Ordinal));
    }

    [Fact]
    public void GuardedProducerRequiresTheSameEstablishedGuard()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        workflow.Steps[0].If = new() { Kind = "input", Source = "enabled" };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
        workflow.Steps[1].If = new() { Kind = "input", Source = "enabled" };
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
    }

    [Fact]
    public void ConditionalSequenceGuardAppliesToItsChildProducers()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var read = workflow.Steps[0];
        workflow.Steps[0] = new() { Key = "guarded", Type = "sequence", If = new() { Kind = "input", Source = "enabled" }, Steps = [read] };
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
        Assert.All(PlanningDataflow.Index(workflow, prep, graph).Values.Where(b => b.Value.Source == "read"), b => Assert.Equal("conditional", b.Availability));
        workflow.Steps[1].If = new() { Kind = "input", Source = "enabled" };
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "read");
    }

    [Fact]
    public void TwoInvalidReferencesArePatchedWithoutReplacingTheirLargeTemplate()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "template", Text = new string('x', 36_000) + "{{a}}{{b}}", Members =
            [new("a", new() { Kind = "output", Source = "read", Path = ["json"] }), new("b", new() { Kind = "output", Source = "read", Path = ["text"] })] }));
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Key = "unit", Kind = "implementation", NodeKeys = ["greeting"], ContractVersion = 2 };
        unit.Candidate = PlanningConstruction.Values(workflow, unit);
        unit.Diagnostics = PlanningGraphValidation.Validate(graph, prep).Where(d => d.Code == "OUTPUT_REFERENCE_INVALID").ToList();
        Assert.Equal(2, unit.Diagnostics.Count);
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, prep, graph));
        var context = patch.Context(unit.Candidate);
        Assert.Equal(2, context.Count); Assert.DoesNotContain(new string('x', 100), context.ToJsonString());
        var changes = new JsonObject(); var binding = PlanningDataflow.Index(workflow, prep, graph, "greeting").Values.Single(b => b.Value.Source == "read");
        foreach (var key in context.Select(p => p.Key)) changes[key] = new JsonObject { ["kind"] = "binding", ["reference"] = binding.Id };
        Assert.True(PlanningConstruction.EstimateInputTokens(context.ToJsonString(), patch.Schema) < 12_000);
        var repaired = patch.Apply(unit.Candidate, new() { ["changes"] = changes, ["remove"] = new JsonArray() });
        var result = PlanningConstruction.Apply(graph, unit, repaired, prep);
        Assert.Equal(workflow.Steps[1].Input.Members[0].Value.Text, result.Workflows[0].Steps[1].Input.Members[0].Value.Text);
        Assert.DoesNotContain(PlanningGraphValidation.Validate(result, prep), d => d.Code == "OUTPUT_REFERENCE_INVALID");
        changes["unrelated"] = true;
        Assert.Throws<InvalidOperationException>(() => patch.Apply(unit.Candidate, new() { ["changes"] = changes, ["remove"] = new JsonArray() }));
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("ressource-modifiée")]
    public async Task TypedArgumentsFollowRuntimeInput_AndPreserveOmission(string resource)
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "implementation", NodeKeys = ["read"], ContractVersion = 2 };
        var binding = PlanningDataflow.Index(workflow, prep, graph, "read").Values.Single(b => b.Value.Source == "resource");
        var candidate = JsonNode.Parse("""{"nodes":{"read":{"arguments":{"resource":{},"optional":{"kind":"omit"}},"onError":[]}},"functions":null}""")!.AsObject();
        candidate["nodes"]!["read"]!["arguments"]!["resource"] = new JsonObject { ["kind"] = "binding", ["reference"] = binding.Id };
        graph = PlanningConstruction.Apply(graph, unit, candidate, prep);
        var factory = new InMemoryMcpClientFactory(); JsonNode? received = null;
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = "inspect", InputSchema = prep.Capabilities[0].InputSchema }], ToolHandlers = new()
            { ["inspect"] = args => { received = args?.DeepClone(); return new McpCallResult { Content = JsonValue.Create("opaque text") }; } } });
        var yaml = new PlanningGraphCompiler().Compile(graph, prep);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var run = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["resource"] = resource }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(resource, received?["resource"]?.ToString()); Assert.Null(received?["optional"]);
        Assert.False(received!.AsObject().ContainsKey("optional"));
        candidate["nodes"]!["read"]!["arguments"]!["optional"] = new JsonObject { ["kind"] = "null" };
        graph = PlanningConstruction.Apply(graph, unit, candidate, prep);
        Assert.Contains(graph.Workflows[0].Steps[0].Input.Members.Single(m => m.Name == "request").Value.Members, m => m.Name == "optional" && m.Value.Kind == "null");
        compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, prep)));
        run = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["resource"] = resource }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.True(received!.AsObject().ContainsKey("optional")); Assert.Null(received["optional"]);
    }

    [Theory]
    [InlineData("value.toUpperCase()")]
    [InlineData("const result = value.toUpperCase(); return result;")]
    public async Task NamedComputationParametersExecuteWithoutImplicitContext(string expression)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = "source", Schema = new() { Type = "string" } });
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "compute", Text = expression, Members = [new("value", new() { Kind = "input", Source = "source" })] }));
        workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, Preparation())));
        var run = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["source"] = "hello" }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal("HELLO", run.Outputs?["message"]?.ToString());
        workflow.Steps[0].Input.Members[0].Value.Text = "data.inputs.source";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "COMPUTATION_BINDING_INVALID");
    }

    [Fact]
    public async Task NestedTemplatesRemainStrings_WhenUsedAsComputationArgumentsAndOutputs()
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = "source", Schema = new() { Type = "string" } });
        var nested = new PlanningValue { Kind = "template", Text = "prefix: {{inner}}", Members =
            [new("inner", new() { Kind = "template", Text = "[{{value}}]", Members = [new("value", new() { Kind = "input", Source = "source" })] })] };
        workflow.Steps[0].Input = Obj(("message", new() { Kind = "compute", Text = "value.toUpperCase()", Members = [new("value", nested)] }));
        workflow.Outputs[0].Value = new() { Kind = "template", Text = "{{result}}!", Members = [new("result", workflow.Outputs[0].Value)] };
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, Preparation())));
        var run = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["source"] = "hello" }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal("PREFIX: [HELLO]!", run.Outputs?["message"]?.ToString());
    }

    [Fact]
    public async Task RuntimeFindingsUseTheSameSmallValueRepairs_AsConstructionFindings()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "template", Text = new string('x', 36_000) + "{{result}}", Members = [new("result", new() { Kind = "output", Source = "read", Path = ["invented"] })] }));
        var state = Session(PlanningStatus.Generating); state.Graph = graph; state.Preparation = prep; state.RepairAttempt = 1;
        state.ConstructionUnits.Add(new() { Key = "late-repair", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion, Calls = 1, Status = "validated" });
        state.Diagnostics = [new("RUNTIME_REFERENCE_INVALID", "/workflows/0/steps/1/input/members/0/value/members/0/value", "Consume the declared whole result.", ValidationStage: PlanningValidationStage.RuntimeContracts)];
        var binding = PlanningDataflow.Index(workflow, prep, graph, "greeting").Values.Single(b => b.Value.Source == "read");
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("repair_unit", phase); Assert.DoesNotContain(new string('x', 100), request.Prompt);
            Assert.True(PlanningConstruction.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()) < 12_000);
            var changes = new JsonObject();
            foreach (var key in request.StructuredOutputSchema["properties"]!["changes"]!["required"]!.AsArray())
                changes[key!.ToString()] = new JsonObject { ["kind"] = "binding", ["reference"] = binding.Id };
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["changes"] = changes, ["remove"] = new JsonArray() } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(state.Status == PlanningStatus.Validating, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic) + "\n" + string.Join("\n", state.Attempts.SelectMany(a => a.Diagnostics).Select(d => d.Message))); Assert.Single(runtime.Requests);
        Assert.Equal(workflow.Steps[1].Input.Members[0].Value.Text, state.Graph!.Workflows[0].Steps[1].Input.Members[0].Value.Text);
    }

    [Fact]
    public void RuntimeArgumentLocationsRespectRetainedTransportMembers()
    {
        var (graph, _) = Fixture(); var node = graph.Workflows[0].Steps[0];
        node.Input.Members.Insert(0, new("raise_on_error", new() { Kind = "boolean", Boolean = false }));
        Assert.Equal("/workflows/0/steps/0/input/members/1/value/members/0/value", TypedWorkflowPlanner.PlanningLocation("/workflows/0/steps/0/input/request/resource", graph));
    }

    [Fact]
    public async Task RepeatedMaterializerBlocksLegacyApprovalBeforeAnyConstructionCall()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var behavior = state.BehaviorPlan!;
        state.Preparation!.Capabilities.Add(new() { Id = "materializer", StepType = "mcp.call", Required = true, OperationIds = ["create"],
            ArtifactContract = new(1, [new("workspace", "/path", "materialize")], []) });
        behavior.Workflows[0].OperationIds.Add("create");
        behavior.Workflows[0].Steps.Add(new() { Key = "create", Kind = "operation", Purpose = "Create artifact", CapabilityId = "materializer" });
        behavior.Workflows[0].Finally.Add(new() { Key = "release", Kind = "operation", Purpose = "Release artifact", CapabilityId = "materializer" });
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(behavior); var approval = state.ApprovedBehaviorHash;
        var runtime = new FakeRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.Equal(PlanningPhase.Behavior, state.CurrentPhase);
        Assert.Equal(approval, state.ApprovedBehaviorHash); Assert.Empty(runtime.Requests);
        Assert.Contains(state.Diagnostics, d => d.Location.EndsWith("/release/capabilityId", StringComparison.Ordinal));
    }

    [Fact]
    public void UpgradePreservesTransportPolicy_AndExposesUndeclaredArgumentsForRepair()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0];
        node.Input.Members.Add(new("raise_on_error", new() { Kind = "boolean", Boolean = false }));
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "implementation", NodeKeys = ["read"], ContractVersion = PlanningDataflow.ContractVersion };
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), prep);
        var upgraded = PlanningConstruction.Apply(graph, unit, candidate, prep);
        Assert.False(upgraded.Workflows[0].Steps[0].Input.Members.Single(m => m.Name == "raise_on_error").Value.Boolean);
        node.Input.Members.Single(m => m.Name == "request").Value.Members.Add(new("undeclared", Str("keep visible")));
        candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), prep);
        Assert.NotNull(candidate["nodes"]!["read"]!["arguments"]!["undeclared"]);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, PlanningConstruction.Schema(workflow, unit, prep, graph), unit));
        unit.Candidate = candidate;
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, prep, graph));
        var repaired = patch.Apply(candidate, new() { ["changes"] = new JsonObject(), ["remove"] = new JsonArray("nodes/read/arguments/undeclared") });
        Assert.Null(repaired["nodes"]!["read"]!["arguments"]!["undeclared"]);
        Assert.True(JsonNode.DeepEquals(candidate["nodes"]!["read"]!["arguments"]!["source"], repaired["nodes"]!["read"]!["arguments"]!["source"]));
    }

    [Fact]
    public void InvalidNestedValuesAreLocatedBeforeFullCompilation()
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input.Members[0].Value.Kind = "invented";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "VALUE_LOWERING_INVALID" && d.Location == "/workflows/0/steps/0/input");
    }

    [Fact]
    public void AcceptedInputDependenciesRejectHardCodedExamples()
    {
        var graph = Graph(); var behavior = BehaviorPlan();
        behavior.Workflows[0].Inputs.Add(new("source", "Dynamic message", true));
        behavior.Workflows[0].Steps[0].InputDependencies = ["source"];
        graph.Workflows[0].Inputs.Add(new() { Name = "source", Schema = new() { Type = "string" } });
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(behavior, graph, Preparation()), d => d.Code == "BUSINESS_INPUT_BINDING_MISSING");
        graph.Workflows[0].Steps[0].Input = Obj(("message", new() { Kind = "input", Source = "source" }));
        Assert.Empty(PlanningBehaviorPlans.ValidateImplementation(behavior, graph, Preparation()));
        var snapshot = Session(); snapshot.Graph = graph; snapshot.Dataflow = PlanningDataflow.Describe(graph, Preparation());
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal(snapshot.Dataflow.Fingerprint, restored.Dataflow!.Fingerprint);
    }

    [Theory]
    [InlineData("'example'")]
    [InlineData("(() => { const source = 'example'; return source; })()")]
    public void DeclaringAnUnusedOrShadowedInputDoesNotEstablishDynamicDataflow(string expression)
    {
        var value = new PlanningValue { Kind = "compute", Text = expression, Members = [new("source", new() { Kind = "input", Source = "url" })] };
        Assert.Throws<InvalidOperationException>(() => PlanningComputations.Validate(value));
    }

    [Fact]
    public async Task ContractUnitsDoNotValidateBusinessBindingsBeforeImplementationExists()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); var behavior = state.BehaviorPlan!;
        behavior.Workflows[0].Inputs.Add(new("source", "Dynamic message", true));
        behavior.Workflows[0].Steps[0].InputDependencies = ["source"];
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(behavior);
        state.Graph = PlanningBehaviorPlans.Display(behavior, state.Preparation);
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase != "fragment_inputs") return Task.FromResult(new LLMResponse { Json = FakeRuntime.ConstructionResponse(request, phase) });
            var workflow = Graph().Workflows[0]; workflow.Inputs.Add(new() { Name = "source", Required = true, Schema = new() { Type = "string" } });
            return Task.FromResult(new LLMResponse { Json = PlanningConstruction.Values(workflow, new() { Kind = "inputs" }) });
        } }; var planner = new TypedWorkflowPlanner();
        for (var i = 0; i < 8 && !state.ConstructionUnits.Any(u => u.Kind == "contracts" && u.Status == "validated"); i++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal("validated", Assert.Single(state.ConstructionUnits, u => u.Kind == "contracts").Status);
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "BUSINESS_INPUT_BINDING_MISSING");
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(behavior, state.Graph!, state.Preparation!), d => d.Code == "BUSINESS_INPUT_BINDING_MISSING");
    }

    [Fact]
    public void StructuredPostProcessingOffersOnlyReferencesValidForStrictOutput()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var capability = prep.Capabilities[0];
        capability.OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"data":{"type":"string"}},"required":[]}""")!.AsObject();
        workflow.Steps[0].StructuredOutput = new(new() { CapabilityId = capability.Id, SchemaPointer = "/output" });
        workflow.Steps[1].OutputSchema = new() { CapabilityId = capability.Id, SchemaPointer = "/output" };
        var unit = new PlanningConstructionUnit { Kind = "contracts", WorkflowKey = workflow.Key, NodeKeys = workflow.Steps.Select(n => n.Key).ToList(), ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(workflow, unit, prep, graph);
        var candidate = PlanningConstruction.Values(workflow, unit);
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        candidate["nodes"]![workflow.Steps[0].Key]!["structuredOutput"] = null;
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        var result = PlanningConstruction.Apply(graph, unit, candidate, prep);
        Assert.Equal(capability.Id, result.Workflows[0].Steps[1].OutputSchema!.CapabilityId);
    }
}
