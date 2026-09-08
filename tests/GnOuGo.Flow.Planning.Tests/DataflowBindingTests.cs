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
    [InlineData("inspect", "prepare", "finish")]
    [InlineData("analyser", "preparer", "terminer")]
    public void ComposedOperationConsumesAllDependenciesAtItsTerminalAndEveryIntermediate(string operation, string firstKey, string lastKey)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        prep.Capabilities = [new() { Id = "first", StepType = "set", OperationIds = [operation], InputOperationIds = ["resource", "analysis"] },
            new() { Id = "last", StepType = "set", OperationIds = [operation], InputOperationIds = ["resource", "analysis"] }];
        PlanningValue Ref(string key) => new() { Kind = "output", Source = key };
        var first = new PlanningNode { Key = firstKey, CapabilityId = "first", OperationIds = [operation], Input = Obj(("resource", Ref("resource"))) };
        var last = new PlanningNode { Key = lastKey, CapabilityId = "last", OperationIds = [operation], Input = Obj(("prepared", Ref(firstKey)), ("analysis", Ref("analysis"))) };
        var group = new PlanningNode { Key = "owned", Type = "sequence", OperationIds = [operation], Steps = [first, last] };
        workflow.Steps = [new() { Key = "resource", OperationIds = ["resource"] }, new() { Key = "analysis", OperationIds = ["analysis"] }, group];
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
        last.Input = Obj(("resource", Ref("resource")), ("analysis", Ref("analysis")));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "COMPOSITION_INPUT_BINDING_MISSING" && d.Message.Contains(firstKey, StringComparison.Ordinal));
        last.Input = Obj(("prepared", Ref(firstKey)));
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING" && d.Message.Contains("analysis", StringComparison.Ordinal));
        last.Input = Obj(("prepared", Ref(firstKey)), ("analysis", Ref("analysis")));
        first.If = new() { Kind = "boolean", Boolean = true };
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Location == "/workflows/0/steps/2/steps/0/input");
        first.If = null; group.OperationIds = ["unrelated_owner"];
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Location == "/workflows/0/steps/2/steps/0/input");
    }

    [Theory]
    [InlineData("perform", "gate")]
    [InlineData("executer", "decision")]
    public void ConditionalProducerDependenciesExposeTheContainingResultWithoutInventingSuccess(string operation, string group)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        workflow.Inputs = [new() { Name = "choice", Schema = new() { Type = "string" } }];
        prep.Capabilities.Add(new() { Id = "consumer", StepType = "set", OperationIds = ["consume"], InputOperationIds = [operation] });
        workflow.Steps[0].CapabilityId = "consumer";
        workflow.Steps.Insert(0, new() { Key = group, Type = "switch", Expr = new() { Kind = "input", Source = "choice" },
            Cases = [new("RUN", null, [new() { Key = "attempt", OperationIds = [operation], Input = Obj(("status", Str("attempted"))) }])] });
        var state = Session(); state.Graph = graph; state.Preparation = prep;
        var unit = new PlanningConstructionUnit { WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"] };
        var context = TypedWorkflowPlanner.ContainerDependencyContext(state, workflow, unit);
        var entry = Assert.Single(context)!;
        Assert.Equal(group, entry["source"]!.ToString()); Assert.Equal("nullable", entry["availability"]!.ToString());
        Assert.Contains(entry["resultContract"]!["anyOf"]!.AsArray(), p => p?["type"]?.ToString() == "null");
        var reference = PlanningDataflow.Index(workflow, prep, graph, "greeting")[entry["binding"]!.ToString()].Value;
        Assert.Contains(PlanningDataflow.OperationInputFindings(graph, prep), d => d.Code == "OPERATION_INPUT_BINDING_MISSING");
        workflow.Steps[1].Input = Obj(("result", new() { Kind = "template", Text = "Outcome: {{result}}", Members = [new("result", reference)] }));
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == "attempt");
    }

    [Theory]
    [InlineData("reader", "parallel")]
    [InlineData("lecteur_renomme", "lectures")]
    public void ParallelBindingsExposeExactBranchProducersAndPreserveRawProvenance(string name, string group)
    {
        var graph = Graph(); var prep = Preparation(); var workflow = graph.Workflows[0];
        prep.Capabilities.Add(new() { Id = "source", StepType = "mcp.call", OutputSchema = new JsonObject { ["type"] = "object",
            ["properties"] = new JsonObject { ["location"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("location") } });
        var read = new PlanningNode { Key = name, Type = "mcp.call", CapabilityId = "source", StructuredOutput = new(new() { Type = "object",
            Properties = [new() { Name = "summary", Required = true, Schema = new() { Type = "string" } }] }) };
        workflow.Steps.Insert(0, new() { Key = group, Type = "parallel", Branches = [new([read]), new([new() { Key = "other", Input = Obj(("unrelated", Str("value"))) }])] });
        var bindings = PlanningDataflow.Index(workflow, prep, graph, "greeting").Values;
        var raw = Assert.Single(bindings, b => b.Value.Source == group && b.Value.Path.SequenceEqual(new[] { "branches", "0", name, "response", "location" }));
        Assert.Equal("string", raw.Schema["type"]!.ToString()); Assert.Equal("unconditional", raw.Availability);
        Assert.Contains(bindings, b => b.Value.Path.SequenceEqual(new[] { "branches", "0", name, "json", "summary" }));
        Assert.DoesNotContain(bindings, b => b.Value.Source == name); // completed group is the boundary
        var resolve = PlanningGraphValidation.ValueContractResolver(graph, workflow, prep);
        Assert.Throws<InvalidOperationException>(() => resolve(new() { Kind = "output", Source = group, Path = ["branches", "1", name, "response", "location"] }));
        Assert.Throws<InvalidOperationException>(() => resolve(new() { Kind = "output", Source = group, Path = ["branches", "2"] }));
        Assert.True(PlanningValueProvenance.Proves(workflow, raw.Value, graph, (node, value) => node == read && value.Path.SequenceEqual(new[] { "location" })));
        Assert.False(PlanningValueProvenance.Proves(workflow, new() { Kind = "output", Source = group, Path = ["branches", "0", name, "json", "summary"] }, graph, (node, _) => node == read));
        read.OnError = [new(null, "continue", Obj(("error", Str("unavailable"))), null)];
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Path.SequenceEqual(raw.Value.Path));
    }

    [Theory]
    [InlineData("context", "summary")]
    [InlineData("contexte", "résumé")]
    public void ComputedObjectMismatchRepairsItsStructureAndPreservesOtherFields(string source, string result)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = source, Schema = new() { Type = "string" } });
        var node = workflow.Steps[0];
        node.Input = Obj((source, new() { Kind = "input", Source = source }));
        node.OutputSchema = new() { Type = "object", Properties = [new() { Name = result, Schema = new() { Type = "string" } }] };
        var finding = Assert.Single(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "SET_OUTPUT_INVALID");
        Assert.EndsWith("/input", finding.Location);
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        unit.Diagnostics = [finding];
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph));
        var coordinate = Assert.Single(patch.Schema["properties"]!["changes"]!["properties"]!.AsObject()).Key;
        Assert.Equal("nodes/" + node.Key + "/input", coordinate);
        Assert.NotNull(TypedWorkflowPlanner.ComputedContractContext(workflow, preparation, patch.Context(unit.Candidate))[node.Key]?["properties"]?[result]);
        var replacement = new JsonObject { ["kind"] = "object", ["members"] = new JsonArray(new JsonObject { ["name"] = result,
            ["value"] = unit.Candidate["nodes"]![node.Key]!["input"]!["members"]![0]!["value"]!.DeepClone() }) };
        var repaired = patch.Apply(unit.Candidate, new JsonObject { ["changes"] = new JsonObject { [coordinate] = replacement }, ["remove"] = new JsonArray() });
        Assert.True(JsonNode.DeepEquals(unit.Candidate["nodes"]![node.Key]!["onError"], repaired["nodes"]![node.Key]!["onError"]));
        var applied = PlanningConstruction.Apply(graph, unit, repaired, preparation);
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(applied, preparation), d => d.Code == "SET_OUTPUT_INVALID");
    }

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
    public void MultiNodeUnitDoesNotOfferLaterBindingsToEarlierOperations()
    {
        var (graph, preparation) = Fixture(); var workflow = graph.Workflows[0];
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["read", "greeting"], ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(workflow, unit, preparation, graph);
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        var future = PlanningDataflow.Index(workflow, preparation, graph, "greeting").Values.Single(b => b.Value.Source == "read");
        candidate["nodes"]!["read"]!["arguments"]!["resource"] = new JsonObject { ["kind"] = "binding", ["reference"] = future.Id };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        var error = Assert.Throws<PlanningDataflow.BindingException>(() => PlanningDataflow.Expand(candidate["nodes"]!["read"]!,
            PlanningDataflow.Index(workflow, preparation, graph, "read"), "/nodes/read"));
        Assert.Equal("/nodes/read/arguments/resource", error.Location);
    }

    [Fact]
    public void LostProducerContractTargetsTheFailureHandlerInsteadOfRegeneratingTheConsumer()
    {
        var (graph, preparation) = Fixture(); var workflow = graph.Workflows[0];
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["read", "greeting"], ContractVersion = PlanningDataflow.ContractVersion };
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        candidate["nodes"]!["read"]!["onError"] = new JsonArray(new JsonObject { ["if"] = null, ["action"] = "continue", ["setOutput"] = new JsonObject { ["kind"] = "object", ["members"] = new JsonArray() }, ["retry"] = null });
        var raw = PlanningDataflow.Index(workflow, preparation, graph, "greeting").Values.Single(b => b.Value.Source == "read");
        candidate["nodes"]!["greeting"]!["input"] = new JsonObject { ["kind"] = "binding", ["reference"] = raw.Id };
        var error = Assert.Throws<PlanningDataflow.BindingException>(() => PlanningConstruction.Apply(graph, unit, candidate, preparation));
        Assert.Equal("/nodes/read/onError", error.Location);
        unit.Candidate = candidate;
        unit.Diagnostics = [new("BINDING_UNAVAILABLE", "/units/unit/candidate" + error.Location, error.Message)];
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph));
        Assert.Equal("nodes/read/onError", Assert.Single(patch.Schema["properties"]!["changes"]!["properties"]!.AsObject()).Key);
        var repaired = patch.Apply(candidate, new JsonObject { ["changes"] = new JsonObject { ["nodes/read/onError"] = new JsonArray() }, ["remove"] = new JsonArray() });
        Assert.True(JsonNode.DeepEquals(candidate["nodes"]!["greeting"], repaired["nodes"]!["greeting"]));
        Assert.Empty(PlanningConstruction.Apply(graph, unit, repaired, preparation).Workflows[0].Steps[0].OnError);
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

    [Fact]
    public void DataComputationCannotSelectAWorkflowIdentifierAsAProducerResult()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        var unit = new PlanningConstructionUnit { WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(workflow, unit, prep, graph);
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), prep);
        candidate["nodes"]!["greeting"]!["input"] = new JsonObject { ["kind"] = "compute", ["text"] = "String(result)", ["members"] = new JsonArray(new JsonObject { ["name"] = "result", ["value"] = new JsonObject { ["kind"] = "workflow", ["source"] = workflow.Key } }) };
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        var binding = PlanningDataflow.Index(workflow, prep, graph, "greeting").Values.Single(b => b.Value.Source == "read");
        candidate["nodes"]!["greeting"]!["input"]!["members"]![0]!["value"] = new JsonObject { ["kind"] = "binding", ["reference"] = binding.Id };
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
    }

    [Theory]
    [InlineData("value.toUpperCase()")]
    [InlineData("const result = value.toUpperCase(); return result;")]
    [InlineData("return [[value]].map(([entry]) => entry.toUpperCase())[0];")]
    [InlineData("const { entry: result } = { entry: value }; return result.toUpperCase();")]
    [InlineData("return (({ entry = '' }, ...suffix) => entry.toUpperCase() + suffix.join(''))({ entry: value });")]
    [InlineData("try { throw new Error(value); } catch (error) { const { message } = error; return message.toUpperCase(); } return '';")]
    [InlineData("decodeURIComponent(encodeURIComponent(value)).toUpperCase()")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeFindingsUseTheSameSmallValueRepairs_AsConstructionFindings(bool wrapped)
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "template", Text = new string('x', 36_000) + "{{result}}", Members = [new("result", new() { Kind = "output", Source = "read", Path = ["invented"] })] }));
        var greeting = workflow.Steps[1];
        if (wrapped) { workflow.Steps[1] = new() { Key = "container", Type = "sequence", Steps = [greeting] }; prep.AllowedStepTypes.Add("sequence"); }
        var state = Session(PlanningStatus.Generating); state.Graph = graph; state.Preparation = prep; state.RepairAttempt = 1;
        state.ConstructionUnits.Add(new() { Key = "late-repair", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"], ContractVersion = PlanningDataflow.ContractVersion, Calls = 1, Status = "validated" });
        state.Diagnostics = [new("RUNTIME_REFERENCE_INVALID", "/workflows/0/steps/1" + (wrapped ? "/steps/0" : "") + "/input/members/0/value/members/0/value", "Consume the declared whole result.", ValidationStage: PlanningValidationStage.RuntimeContracts)];
        if (wrapped) state.Diagnostics.Insert(0, new("SCENARIO_EXECUTION_FAILED", "/workflows/0/steps/1", "The container failed because its child failed."));
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
        Assert.Equal(greeting.Input.Members[0].Value.Text, PlanningGraphCompiler.Enumerate(state.Graph!.Workflows[0].Steps).Single(n => n.Key == "greeting").Input.Members[0].Value.Text);
    }

    [Fact]
    public async Task SemanticFailureHandlingFinding_UsesTargetedErrorHandlerPatch()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        var state = Session(PlanningStatus.Generating); state.Graph = graph; state.Preparation = prep; state.RepairAttempt = 1;
        state.ConstructionUnits.Add(new() { Key = "late-repair", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["read"], ContractVersion = PlanningDataflow.ContractVersion, Calls = 1, Status = "validated" });
        state.Diagnostics = [new("SEMANTIC_FAILURE_HANDLING", "/workflows/0/steps/0/onError", "Capture a non-artifact failure result so later review remains reachable.")];
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("repair_unit", phase);
            var required = request.StructuredOutputSchema!["properties"]!["changes"]!["required"]!.AsArray();
            Assert.Equal("nodes/read/onError", Assert.Single(required)!.GetValue<string>());
            return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("""{"changes":{"nodes/read/onError":[{"if":null,"action":"continue","setOutput":{"kind":"null"},"retry":null}]},"remove":[]}""") });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Validating, state.Status); Assert.Single(runtime.Requests);
        Assert.Equal("continue", Assert.Single(state.Graph!.Workflows[0].Steps[0].OnError).Action);
    }

    [Fact]
    public void TemplateRepairUsesCandidateArgumentOrder_InsteadOfEmptyRetainedSkeleton()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        var node = workflow.Steps[0]; node.Input = Obj();
        var unit = new PlanningConstructionUnit { Key = "repair", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["read"], ContractVersion = PlanningDataflow.ContractVersion };
        var binding = PlanningDataflow.Index(workflow, prep, graph, "read").Values.Single(b => b.Value.Source == "resource");
        unit.Candidate = JsonNode.Parse("""{"nodes":{"read":{"arguments":{"optional":{"kind":"omit"},"resource":{"kind":"template","text":"Resource ${resource}","members":[{"name":"resource","value":{}}]}},"onError":[]}},"functions":null}""")!.AsObject();
        unit.Candidate["nodes"]!["read"]!["arguments"]!["resource"]!["members"]![0]!["value"] = new JsonObject { ["kind"] = "binding", ["reference"] = binding.Id };
        var candidate = PlanningConstruction.Apply(graph, unit, unit.Candidate, prep);
        unit.Diagnostics = PlanningExecutableValidation.Validate(candidate, prep).Where(d => d.Code is "TEMPLATE_BINDING_INVALID" or "VALUE_LOWERING_INVALID").ToList();
        Assert.NotEmpty(unit.Diagnostics);
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, prep, graph), prep);
        var coordinates = patch.Context(unit.Candidate).Select(p => p.Key).ToArray();
        Assert.Contains("nodes/read/arguments/resource", coordinates);
        Assert.DoesNotContain(coordinates, p => p.Contains("/members/", StringComparison.Ordinal));
        Assert.Empty(node.Input.Members);
    }

    [Theory]
    [InlineData("analysis", "select")]
    [InlineData("analyse", "sélection")]
    public void LockedProducerDependencyRejectsUnrelatedData_AndAcceptsItsAlias(string sourceOperation, string consumerOperation)
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        prep.Capabilities[0].OperationIds = [sourceOperation];
        prep.Capabilities.Add(new() { Id = "local", StepType = "set", OperationIds = [consumerOperation], InputOperationIds = [sourceOperation] });
        workflow.Steps[1].CapabilityId = "local";
        workflow.Steps[1].Input = Obj(("message", new() { Kind = "input", Source = "resource" }));
        var finding = Assert.Single(PlanningDataflow.OperationInputFindings(graph, prep));
        Assert.Equal("/workflows/0/steps/1/input", finding.Location); Assert.Contains("read", finding.Message);
        workflow.Steps.Insert(1, new() { Key = "alias", Input = Obj(("value", new() { Kind = "output", Source = "read" })) });
        workflow.Steps[2].Input = Obj(("message", new() { Kind = "output", Source = "alias", Path = ["value"] }));
        Assert.Empty(PlanningDataflow.OperationInputFindings(graph, prep));
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

    [Fact]
    public void ConstructionWrapsOnlySchemaValidatedLiteralStructuredFallbacks()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0];
        node.StructuredOutput = new(new() { Type = "object", Properties = [
            new() { Name = "accepted", Required = true, Schema = new() { Type = "boolean" } },
            new() { Name = "details", Required = true, Schema = new() { Type = "string" } }] });
        var value = Obj(("accepted", new() { Kind = "boolean", Boolean = false }), ("details", Str("Execution failed.")));
        node.OnError = [new(null, "continue", value, null)];
        var unit = new PlanningConstructionUnit { Kind = "implementation", WorkflowKey = workflow.Key, NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        PlanningGraph Apply() => PlanningConstruction.Apply(graph, unit,
            PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), prep), prep);
        var applied = Apply(); var handler = applied.Workflows[0].Steps[0].OnError[0];
        Assert.Equal("continue", handler.Action);
        Assert.Equal("json", Assert.Single(handler.SetOutput!.Members).Name);
        Assert.True(JsonNode.DeepEquals(PlanningGraphValidation.Literal(value), PlanningGraphValidation.Literal(handler.SetOutput.Members[0].Value)));
        Assert.DoesNotContain(PlanningGraphValidation.Validate(applied, prep), d => d.Code == "STRUCTURED_FALLBACK_INVALID");
        Assert.Equal(2, node.OnError[0].SetOutput!.Members.Count); // Candidate/history remain unchanged.
        node.OnError = [new(null, "continue", Obj(("accepted", Str("false"))), null)];
        Assert.Contains(PlanningGraphValidation.Validate(Apply(), prep), d => d.Code == "STRUCTURED_FALLBACK_INVALID");
        node.OnError = [new(null, "continue", Obj(("json", value), ("response", Obj())), null)];
        Assert.Equal(2, Apply().Workflows[0].Steps[0].OnError[0].SetOutput!.Members.Count);
    }

    [Fact]
    public void StructuredFallbackRepairsOnlyTheJsonValue_AndKeepsTheErrorAction()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0];
        node.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "items", Required = true, Schema = new() { Type = "array", Items = new() { Type = "string" } } }] });
        node.OnError = [new(null, "continue", Obj(("response", Obj()), ("json", Str("{\"items\":[]}"))), null)];
        var unit = new PlanningConstructionUnit { Kind = "implementation", WorkflowKey = workflow.Key, NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), prep);
        unit.Diagnostics = PlanningGraphValidation.Validate(graph, prep).Where(d => d.Code == "STRUCTURED_FALLBACK_INVALID").ToList();
        Assert.Single(unit.Diagnostics);
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, prep, graph));
        var field = Assert.Single(patch.Context(unit.Candidate)).Key;
        Assert.EndsWith("/onError/0/setOutput/members/1/value", field);
        var destination = TypedWorkflowPlanner.FallbackContractContext(workflow, prep, patch.Context(unit.Candidate));
        Assert.Equal("array", destination[node.Key]!["json"]!["properties"]!["items"]!["type"]!.GetValue<string>());
        var fixedValue = PlanningModelValues.Compact(JsonSerializer.SerializeToNode(Obj(("items", new() { Kind = "array" })), PlanningJsonContext.Default.PlanningValue));
        var candidate = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [field] = fixedValue }, ["remove"] = new JsonArray() });
        var repaired = PlanningConstruction.Apply(graph, unit, candidate, prep);
        Assert.Equal("continue", repaired.Workflows[0].Steps[0].OnError[0].Action);
        Assert.DoesNotContain(PlanningGraphValidation.Validate(repaired, prep), d => d.Code == "STRUCTURED_FALLBACK_INVALID");
    }

    [Fact]
    public void RawBindingIsUnavailableWhenContinuationOnlyProducesStructuredJson()
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var node = workflow.Steps[0];
        node.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "ok", Required = true, Schema = new() { Type = "boolean" } }] });
        node.OnError = [new(null, "continue", Obj(("json", Obj(("ok", new() { Kind = "boolean", Boolean = false })))), null)];
        var bindings = PlanningDataflow.Index(workflow, prep, graph, "greeting");
        Assert.DoesNotContain(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel is null or "default");
        Assert.Contains(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel == "structured");
        Assert.Contains(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel == "envelope" && b.Value.Path.Count == 0);
        Assert.DoesNotContain(bindings.Values, b => b.Value.Source == node.Key && b.Value.ResultChannel == "envelope" && b.Value.Path.FirstOrDefault() == "response");
        node.OnError[0].SetOutput!.Members.Add(new("response", Obj()));
        Assert.Contains(PlanningDataflow.Index(workflow, prep, graph, "greeting").Values, b => b.Value.Source == node.Key && b.Value.ResultChannel is null or "default");
    }

    [Fact]
    public void PromptSchemaCompactionPreservesConstraintsAndRemovesPlanningMetadata()
    {
        var (graph, prep) = Fixture(); var node = graph.Workflows[0].Steps[0];
        node.StructuredOutput = new(new() { Type = "object", Properties = Enumerable.Range(0, 24).Select(i => new PlanningPort
            { Name = "field" + i, Required = true, Schema = new() { Type = "string", Nullable = true, Enum = ["one", "two"] } }).ToList() });
        var verbose = TypedWorkflowPlanner.DescribeNode(node);
        var compact = TypedWorkflowPlanner.DescribeNode(node, prep);
        Assert.True(compact.ToJsonString().Length < verbose.ToJsonString().Length / 2);
        Assert.True(JsonNode.DeepEquals(PlanningGraphCompiler.ToJsonSchema(node.StructuredOutput.Schema, prep), compact["structuredOutput"]!["schema"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedBindingContextPreservesEveryIdentifierAndItsTypeAndAvailability(bool parallel)
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0];
        workflow.Steps[0].StructuredOutput = new(new() { Type = "object", Properties = Enumerable.Range(0, 30).Select(i => new PlanningPort
            { Name = "field" + i, Required = true, Schema = new() { Type = "string", Nullable = true } }).ToList() });
        if (parallel) workflow.Steps[0] = new() { Key = "nested_parallel_result", Type = "parallel", Branches = [new([workflow.Steps[0]])] };
        var state = Session(); state.Graph = graph; state.Preparation = prep;
        var bindings = PlanningDataflow.CompactIndex(workflow, prep, graph, "greeting");
        var context = TypedWorkflowPlanner.BindingContext(state, workflow, new() { NodeKeys = ["greeting"] });
        var entries = context.SelectMany(g => g!["bindings"]!.AsArray()).ToArray();
        Assert.Equal(bindings.Count, entries.Length);
        foreach (var group in context)
        foreach (var entry in group!["bindings"]!.AsArray())
        {
            var original = bindings[entry![0]!.GetValue<string>()];
            var prefix = (group["pathPrefix"] as JsonArray ?? []).Select(p => p!.GetValue<string>());
            Assert.Equal(original.Value.Path, prefix.Concat(entry[1]!.AsArray().Select(p => p!.GetValue<string>())).ToList());
            Assert.Equal(original.Schema["type"]?.ToJsonString() ?? "\"unknown\"", entry[2]!.ToJsonString());
            Assert.Equal(original.Availability, entry[3]!.GetValue<string>());
        }
        if (parallel)
        {
            Assert.Contains(context, g => g?["pathPrefix"] is JsonArray { Count: > 1 });
            var expanded = context.DeepClone().AsArray();
            foreach (var group in expanded)
            {
                var prefix = (group!["pathPrefix"] as JsonArray ?? []).Select(p => p!.GetValue<string>()).ToArray();
                foreach (var entry in group["bindings"]!.AsArray()) entry![1] = new JsonArray(prefix.Concat(entry[1]!.AsArray().Select(p => p!.GetValue<string>())).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
                group.AsObject().Remove("pathPrefix");
            }
            Assert.True(context.ToJsonString().Length < expanded.ToJsonString().Length);
        }
    }

    [Theory]
    [InlineData("producer-a", "consumer-a", "artifact.alpha")]
    [InlineData("producteur-renomme", "consommateur-renomme", "objet.beta")]
    public void ArtifactArgumentsSelectOriginalProducerBindings_WithoutAcceptingStructuredCopies(string producerId, string consumerId, string artifactKind)
    {
        var (graph, prep) = Fixture(); var workflow = graph.Workflows[0]; var producer = workflow.Steps[0]; var consumer = workflow.Steps[1];
        var contract = JsonNode.Parse("""{"type":"object","properties":{"handle":{"type":"string"}},"required":["handle"],"additionalProperties":false}""")!.AsObject();
        prep.Capabilities[0].Id = producer.CapabilityId = producerId;
        prep.Capabilities[0].OutputSchema = contract;
        prep.Capabilities[0].ArtifactContract = new(1, [new(artifactKind, "/handle", "materialize")], []);
        producer.StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "handle", Required = true, Schema = new() { Type = "string" } }] });
        prep.Capabilities.Add(new() { Id = consumerId, StepType = "mcp.call", Server = consumerId, Method = "consume", InputSchema = contract.DeepClone().AsObject(), ArtifactContract = new(1, [], [new(artifactKind, "/handle", true)]) });
        consumer.Type = "mcp.call"; consumer.CapabilityId = consumerId;
        consumer.Input = Obj(("request", Obj(("handle", new() { Kind = "output", Source = producer.Key, Path = ["handle"] }))));
        var unit = new PlanningConstructionUnit { Kind = "implementation", WorkflowKey = workflow.Key, NodeKeys = [consumer.Key], ContractVersion = PlanningDataflow.ContractVersion };
        var schema = PlanningConstruction.Schema(workflow, unit, prep, graph);
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), prep);
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        candidate["nodes"]![consumer.Key]!["arguments"]!["handle"]!["reference"] = PlanningOutputBindings.Id(new() { Kind = "output", Source = producer.Key, Path = ["handle"], ResultChannel = "structured" });
        Assert.NotEmpty(PlanningConstruction.ShapeFindings(candidate, schema, unit));
        producer.Type = "set"; producer.Input = Obj(("handle", Str("invented artifact")));
        Assert.Throws<PlanningArtifactBindings.UnresolvedArtifactException>(() => PlanningConstruction.Schema(workflow, unit, prep, graph));
    }
}
