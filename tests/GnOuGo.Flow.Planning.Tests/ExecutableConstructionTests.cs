using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ExecutableConstructionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static JsonObject Patch(string? node, string field, JsonNode? value) => new() { ["target"] = "f_" + PlanningGraphCompiler.Fingerprint(PlanningPatches.Coordinate("main", node, field))[..16], ["value"] = value };
    private static JsonObject Changes(params JsonObject[] patches) => new() { ["patches"] = new JsonArray(patches.Select(p => (JsonNode)p).ToArray()) };
    private static PlanningSchema ObjectSchema(params (string Name, string Type)[] fields) => new() { Type = "object", Properties = fields.Select(p => new PlanningPort { Name = p.Name, Schema = new() { Type = p.Type } }).ToList() };

    [Fact]
    public void GlobalFunctionsStayFrozenWhileAnExactConditionTextCanBeRepaired()
    {
        var graph = Graph(); graph.Functions = "invalid code";
        var node = graph.Workflows[0].Steps[0]; node.Type = "switch"; node.Cases = [new("yes", new() { Kind = "expression", Text = "1 +" }, [])];
        var scope = PlanningPatches.Scope(graph, [new("FUNCTION_SYNTAX_INVALID", "/functions", "Invalid"), new("EXPR_PARSE", "/workflows/0/steps/0/cases/0/when/text", "Invalid")]);
        Assert.Single(scope);
        var candidate = PlanningPatches.Apply(graph, Changes(Patch("greeting", "cases/0/when/text", "1 + 1")), scope, Preparation());
        Assert.Equal("invalid code", candidate.Functions);
        Assert.Equal("1 + 1", candidate.Workflows[0].Steps[0].Cases[0].When!.Text);
    }

    [Fact]
    public void ConsumerFindingDoesNotGrantProducerOrGuardEdits()
    {
        var graph = Graph(); var child = graph.Workflows[0].Steps[0];
        graph.Workflows[0].Steps = [new() { Key = "loop", Type = "loop.sequential", Steps = [child] }];
        var scope = PlanningPatches.Scope(graph, [new("ARTIFACT_UNPROVEN", "/workflows/0/steps/0/steps/0/input/request/value", "Needs its item source")]);
        Assert.DoesNotContain(PlanningPatches.Coordinate("main", "loop", "input"), scope);
        Assert.DoesNotContain(PlanningPatches.Coordinate("main", "loop", "if"), scope);
        Assert.DoesNotContain(PlanningPatches.Coordinate("main", "loop", "expr"), scope);
    }

    [Fact]
    public void UnlocatedComputationDoesNotGrantHelperEdits()
    {
        var graph = Graph(); graph.Functions = "function unrelated() { return 'safe'; }";
        graph.Workflows[0].Functions = "function compute(value) { return value; }";
        graph.Workflows[0].Steps[0].Input = Obj(("message", new() { Kind = "expression", Text = "compute(data.inputs.value)" }));
        var scope = PlanningPatches.Scope(graph, [new("SCENARIO_EXECUTION_FAILED", "/workflows/0/steps/0", "Computation failed")]);
        Assert.DoesNotContain(PlanningPatches.Coordinate("main", null, "functions"), scope);
        Assert.DoesNotContain(PlanningPatches.Coordinate(null, null, "functions"), scope);
    }

    [Fact]
    public void RecordedDefects_AreCollectedTogetherBeforeRepair()
    {
        var graph = Graph();
        graph.Workflows[0].Functions = "function compute(input) { inspect input; return a validated result; }";
        var node = graph.Workflows[0].Steps[0];
        node.Expr = new() { Kind = "expression", Text = "compute({value: input.value})" };
        node.OutputSchema = ObjectSchema(("invented", "integer"));
        graph.Workflows[0].Steps.Add(new() { Key = "confirm", Type = "human.input", Input = Obj(("mode", Str("confirm")), ("prompt", Str("Allow the external write?"))) });
        var errors = PlanningExecutableValidation.Validate(graph, Preparation());
        Assert.Contains(errors, d => d.Code == "FUNCTION_SYNTAX_INVALID");
        Assert.Contains(errors, d => d.Code == "NATIVE_FIELD_UNSUPPORTED");
        Assert.Contains(errors, d => d.Code == "SET_OUTPUT_INVALID");
        Assert.Contains(errors, d => d.Message.Contains("choices", StringComparison.Ordinal));
        Assert.Contains(errors, d => d.Message.Contains("Unsupported context alias", StringComparison.Ordinal));
    }

    [Fact]
    public void UndiagnosedLiteralSchemaAnnotationCannotBePatched()
    {
        var original = Graph();
        var schema = JsonNode.Parse("""{"kind":"inline","type":"object","nullable":false,"description":null,"enum":[],"properties":[{"name":"message","required":true,"default":null,"schema":{"kind":"inline","type":"string","nullable":false,"description":null,"enum":[],"properties":[],"items":null,"additionalProperties":null}}],"items":null,"additionalProperties":null}""");
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(original, Changes(Patch("greeting", "outputSchema", schema)), new HashSet<string>(), Preparation()));
        Assert.Null(original.Workflows[0].Steps[0].OutputSchema);
    }

    [Fact]
    public void AtomicPatchesRejectDuplicatesAndUnscopedChanges()
    {
        var graph = Graph(); var before = PlanningGraphCompiler.Fingerprint(graph);
        var input = PlanningFixtures.Workflow(graph.Workflows[0])["steps"]![0]!["input"]!.DeepClone();
        var allowed = new HashSet<string> { PlanningPatches.Coordinate("main", "greeting", "input/members/0/value/text") };
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(graph, Changes(Patch("greeting", "input/members/0/value/text", "Fixed"), Patch("greeting", "input/members/0/value/text", "Again")), allowed, Preparation()));
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(graph));
    }

    [Fact]
    public void UndiagnosedProducerSchemaCannotBeRemoved()
    {
        var graph = Graph(); var preparation = Preparation();
        var node = graph.Workflows[0].Steps[0]; node.Type = "mcp.call"; node.CapabilityId = "renamed";
        preparation.Capabilities.Add(new() { Id = "renamed", StepType = "mcp.call", Server = "host", Method = "tool", Kind = "tool", InputSchema = new() { ["type"] = "object" }, OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["message"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("message") } });
        node.Input = Obj(); node.OutputSchema = new() { CapabilityId = "renamed", SchemaPointer = "/output" };
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(graph, Changes(Patch("greeting", "outputSchema", null)), new HashSet<string>(), preparation));
        graph.Workflows[0].Outputs[0].Value.Path = ["invented"];
        Assert.Contains(PlanningGraphValidation.Validate(graph, preparation), d => d.Code == "OUTPUT_REFERENCE_INVALID");
    }

    [Fact]
    public async Task LogicalChildReferencesAreLoweredInsideContainersWithoutRenamingPayloadFields()
    {
        var graph = Graph();
        var child = graph.Workflows[0].Steps[0]; child.Input = Obj(("greeting", Str("Hello")));
        graph.Workflows[0].Steps = [new() { Key = "container", Type = "sequence", Steps = [child] }];
        graph.Workflows[0].Outputs[0].Value = new() { Kind = "expression", Text = "data.steps.container['greeting'].greeting + ' greeting'" };
        var yaml = new PlanningGraphCompiler().Compile(graph, Preparation());
        Assert.DoesNotContain("['greeting']", yaml);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("Hello greeting", result.Outputs?["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task CorrectSetComputationsAndTemplateBindings_Execute()
    {
        var graph = Graph(); var node = graph.Workflows[0].Steps[0];
        node.Input = Obj(("message", new() { Kind = "expression", Text = "({nested:{value:'Hello'}}).nested.value" }));
        node.OutputSchema = ObjectSchema(("message", "string"));
        graph.Workflows[0].Steps.Add(new() { Key = "render", Input = Obj(("text", new() { Kind = "template", Text = "Message: {{greeting}}", Members = [new("greeting", new() { Kind = "output", Source = "greeting", Path = ["message"] })] })) });
        graph.Workflows[0].Outputs[0].Value.Source = "render"; graph.Workflows[0].Outputs[0].Value.Path = ["text"];
        var result = await Execute(graph, Preparation());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("Message: Hello", result.Outputs?["message"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("({message:'Hello'})", true)]
    [InlineData("({message:42})", false)]
    public async Task WholeSetExpressionsAreValidatedByTheirRuntimeAssertion(string expression, bool success)
    {
        var graph = Graph(); var node = graph.Workflows[0].Steps[0];
        node.Input = new() { Kind = "expression", Text = expression }; node.OutputSchema = ObjectSchema(("message", "string"));
        Assert.Empty(PlanningExecutableValidation.Validate(graph, Preparation()));
        var result = await Execute(graph, Preparation());
        Assert.Equal(success, result.Success);
    }

    [Theory]
    [InlineData("data.steps['greeting'].message + ' data.steps.unrelated'", "Hello data.steps.unrelated")]
    [InlineData("data['steps']['greeting'].message /* data.steps.unrelated */", "Hello")]
    [InlineData("((data) => data.steps.local)({steps:{local:'local value'}})", "local value")]
    public async Task ExpressionBindingsUseSyntaxAndPreserveLiteralText(string expression, string expected)
    {
        var graph = Graph();
        graph.Workflows[0].Outputs[0].Value = new() { Kind = "expression", Text = expression };
        var result = await Execute(graph, Preparation());
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expected, result.Outputs?["message"]?.GetValue<string>());
    }

    [Fact]
    public void MissingTemplateBindings_AreActionableFindings()
    {
        var graph = Graph(); graph.Workflows[0].Steps[0].Input = Obj(("message", new() { Kind = "template", Text = "{{undeclared}}" }));
        Assert.Contains(PlanningExecutableValidation.Validate(graph, Preparation()), d => d.Code == "TEMPLATE_BINDING_INVALID");
    }

    [Fact]
    public void CompilerFindingsUseWorkflowIdentityWhenNodeKeysRepeat()
    {
        var graph = Graph(); var other = Graph().Workflows[0]; other.Key = "other"; graph.Workflows.Add(other);
        var errors = new WorkflowCompilationException([new() { Code = "EXPR_PARSE", WorkflowName = "w_" + PlanningGraphCompiler.Fingerprint("other")[..16], StepId = "n_" + PlanningGraphCompiler.Fingerprint("greeting")[..16], Field = "input.message", Message = "Invalid expression" }]);
        Assert.Equal("/workflows/1/steps/0/input/message", Assert.Single(PlanningExecutableValidation.CompilerErrors(errors, graph)).Location);
    }

    [Theory]
    [InlineData("workflow:main/field:functions.compute", "/workflows/0/functions/compute", null, "functions")]
    [InlineData("workflow:main/field:outputs.message", "/workflows/0/outputs/0", null, "outputs")]
    public void UnlocatedRuntimeContractFindingsGrantNoContainerPermission(string location, string expected, string? node, string field)
    {
        var graph = Graph(); var finding = PlanningExecutableValidation.MapRuntimeDiagnostic(new("CONTRACT_INVALID", location, "Invalid contract"), graph);
        Assert.Equal(expected, finding.Location);
        Assert.DoesNotContain(PlanningPatches.Coordinate("main", node, field), PlanningPatches.Scope(graph, [finding]));
        Assert.Empty(PlanningPatches.Scope(graph, [finding]));
    }

    [Fact]
    public void RuntimeDecisionLineageFindingAllowsItsExpressionButNoNewGuard()
    {
        var graph = Graph(); var node = graph.Workflows[0].Steps[0]; node.Type = "switch";
        var diagnostic = PlanningExecutableValidation.MapRuntimeDiagnostic(new("CONTRACT_INVALID", "workflow:main/step:n_" + PlanningGraphCompiler.Fingerprint(node.Key)[..16] + "/field:expr", "Use the declared boundary"), graph);
        var scope = PlanningPatches.Scope(graph, [diagnostic]);
        Assert.Contains(PlanningPatches.Coordinate("main", node.Key, "expr"), scope);
        Assert.DoesNotContain(PlanningPatches.Coordinate("main", node.Key, "if"), scope);
    }

    [Theory]
    [InlineData("accept", "certain", false, 1, true)]
    [InlineData("reject", "certain", false, 0, true)]
    [InlineData("accept", "uncertain", false, 0, true)]
    [InlineData("accept", "certain", true, 1, false)]
    public async Task TypedConfirmationAndUncertainDefaultControlWritesAndAlwaysCleanUp(string response, string certainty, bool failWrite, int expectedWrites, bool expectedSuccess)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var preparation = Preparation();
        var writes = 0; var cleanups = 0;
        foreach (var id in new[] { "write", "cleanup" }) preparation.Capabilities.Add(new() { Id = id, StepType = "mcp.call", Server = "renamed", Method = id, Kind = "tool", InputSchema = new() { ["type"] = "object" } });
        workflow.Outputs.Clear();
        workflow.Steps = [
            new() { Key = "confirm", Type = "human.input", Input = Obj(("mode", Str("confirm")), ("prompt", Str("Allow the write?")), ("choices", new() { Kind = "array", Items = [Str("accept"), Str("reject")] })) },
            new() { Key = "decision", Type = "switch", Cases = [new(null, new() { Kind = "expression", Text = "data.steps.confirm.response === true && '" + certainty + "' === 'certain'" }, [new() { Key = "write", Type = "mcp.call", CapabilityId = "write", Input = Obj() }])] }];
        workflow.Finally = [new() { Key = "cleanup", Type = "mcp.call", CapabilityId = "cleanup", Input = Obj() }];
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed", new()
        {
            Tools = [new() { Name = "write", InputSchema = new JsonObject { ["type"] = "object" } }, new() { Name = "cleanup", InputSchema = new JsonObject { ["type"] = "object" } }],
            ToolHandlers = new()
            {
                ["write"] = _ => { writes++; if (failWrite) throw new InvalidOperationException("Simulated write failure"); return new McpCallResult { Content = new JsonObject { ["ok"] = true } }; },
                ["cleanup"] = _ => { cleanups++; return new McpCallResult { Content = new JsonObject { ["ok"] = true } }; }
            }
        });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        var result = await new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new Confirm(response) }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(expectedSuccess == result.Success, result.Error?.Message); Assert.Equal(expectedWrites, writes); Assert.Equal(1, cleanups);
    }

    private sealed class Confirm(string response) : IHumanInputProvider
    {
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct) => Task.FromResult<JsonNode?>(JsonValue.Create(response));
    }

    [Theory]
    [InlineData("renamed", "first", false)]
    [InlineData("another", "second", true)]
    public async Task StoppingStructuredCallDiscardsFallbackAndCannotEnableLaterWrites(string server, string method, bool absent)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; var preparation = Preparation();
        var writes = 0; var cleanups = 0;
        foreach (var id in new[] { method, "write", "cleanup" }) preparation.Capabilities.Add(new()
        { Id = id, StepType = "mcp.call", Server = server, Method = id, Kind = "tool", InputSchema = new() { ["type"] = "object" } });
        workflow.Outputs.Clear();
        var producer = new PlanningNode
        {
            Key = "producer",
            Type = "mcp.call",
            CapabilityId = method,
            Input = Obj(),
            StructuredOutput = new(new() { Type = "object", Properties = [new() { Name = "accepted", Required = true, Schema = new() { Type = "boolean" } }] }),
            OnError = [new(null, "stop", absent ? null : Obj(("failure", Str("Unavailable"))), null)]
        };
        workflow.Steps = [producer, new() { Key = "write", Type = "mcp.call", CapabilityId = "write", Input = Obj() }];
        workflow.Finally = [new() { Key = "cleanup", Type = "mcp.call", CapabilityId = "cleanup", Input = Obj() }];
        Assert.Empty(PlanningGraphValidation.Validate(graph, preparation));
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, new()
        {
            Tools = preparation.Capabilities.Select(c => new McpToolInfo { Name = c.Method!, InputSchema = c.InputSchema }).ToList(),
            ToolHandlers = new()
            {
                [method] = _ => throw new InvalidOperationException("Simulated producer failure"),
                ["write"] = _ => { writes++; return new McpCallResult(); },
                ["cleanup"] = _ => { cleanups++; return new McpCallResult(); }
            }
        });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.False(result.Success); Assert.Equal(0, writes); Assert.Equal(1, cleanups);
        Assert.Null(result.StepResults[0].Output);
        producer.OnError = [producer.OnError[0] with { Action = "continue" }];
        Assert.Contains(PlanningGraphValidation.Validate(graph, preparation), d => d.Code == "STRUCTURED_FALLBACK_INVALID");
        producer.OnError = [new(null, "stop", new() { Kind = "output", Source = "missing" }, null)];
        Assert.Contains(PlanningGraphValidation.Validate(graph, preparation), d => d.Location.Contains("/onError/0/setOutput", StringComparison.Ordinal));
    }

    [Fact]
    public void ConditionalNativeOutputs_CannotInventAnAlwaysPresentField()
    {
        var graph = Graph(); var child = graph.Workflows[0].Steps[0];
        graph.Workflows[0].Steps = [new() { Key = "decision", Type = "switch", Cases = [new(null, new() { Kind = "expression", Text = "data.inputs.allow" }, [child])] }];
        graph.Workflows[0].Inputs = [new() { Name = "allow", Schema = new() { Type = "boolean" } }];
        graph.Workflows[0].Outputs[0].Value = new() { Kind = "output", Source = "decision", Path = ["greeting", "message"] };
        Assert.Contains(PlanningGraphValidation.Validate(graph, Preparation()), d => d.Code == "OUTPUT_REFERENCE_INVALID");
    }

    [Fact]
    public void BehaviorWorkflowCalls_AreExplicitReachableAndAcyclic()
    {
        var plan = BehaviorPlan(); var preparation = Preparation();
        plan.Workflows.Add(new() { Key = "child", Purpose = "Compute a value", Steps = [new() { Key = "value", Purpose = "Return a value" }] });
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Message.Contains("reachable", StringComparison.Ordinal));
        plan.Workflows[0].Steps.Add(new() { Key = "call", Kind = "workflow", WorkflowKey = "child", Purpose = "Call child" });
        Assert.Empty(PlanningBehaviorPlans.Validate(plan, preparation));
        Assert.Equal("workflow.call", PlanningBehaviorPlans.Display(plan, preparation).Workflows[0].Steps[^1].Type);
        plan.Workflows[1].Steps.Add(new() { Key = "cycle", Kind = "workflow", WorkflowKey = "main", Purpose = "Call main" });
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Message.Contains("cycle", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("contract_A", "required_A")]
    [InlineData("renamed_contract", "obligation_renamed")]
    public void MandatoryOwnershipIsDerivedOnlyFromOneExactCapabilityOwner(string capabilityId, string operation)
    {
        var plan = BehaviorPlan(); var preparation = Preparation();
        preparation.Capabilities.Add(new() { Id = capabilityId, StepType = "human.input", Required = true, OperationIds = [operation] });
        plan.Workflows[0].Steps.Add(new() { Key = "confirm", Kind = "confirmation", Purpose = "Confirm the effect", CapabilityId = capabilityId });
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.Contains(operation, plan.Workflows[0].OperationIds);
        Assert.Empty(PlanningBehaviorPlans.Validate(plan, preparation));
        plan.Workflows[0].OperationIds.Clear();
        plan.Workflows.Add(new() { Key = "other", Steps = [new() { Key = "other-confirm", CapabilityId = capabilityId }] });
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.All(plan.Workflows, w => Assert.DoesNotContain(operation, w.OperationIds));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("loop.sequential")]
    [InlineData("switch")]
    [InlineData("parallel")]
    public async Task NativeContainers_DeriveRealProducerContractsAndAddresses(string type)
    {
        var graph = Graph(); var child = graph.Workflows[0].Steps[0];
        var parent = new PlanningNode { Key = "container", Type = type };
        if (type == "switch") { parent.Expr = Str("selected"); parent.Cases = [new("selected", null, [child])]; }
        else if (type == "parallel") parent.Branches = [new([child])];
        else parent.Steps = [child];
        if (type == "loop.sequential") parent.Input = Obj(("times", new() { Kind = "number", Number = 1 }));
        graph.Workflows[0].Steps = [parent];
        graph.Workflows[0].Outputs[0].Value = new() { Kind = "output", Source = "container", Path = type == "parallel" ? ["branches", "0", "greeting", "message"] : type == "loop.sequential" ? ["results", "0", "greeting", "message"] : ["greeting", "message"] };
        var result = await Execute(graph, Preparation());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("Hello", result.Outputs?["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task TypedStructuredOutputAndOriginalMcpSchema_StaySeparate()
    {
        var preparation = Preparation(); var graph = Graph();
        var output = JsonNode.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"],"additionalProperties":false}""")!.AsObject();
        preparation.Capabilities.Add(new() { Id = "cap", StepType = "mcp.call", Server = "renamed-host", Method = "renamed-method", Kind = "tool", InputSchema = new() { ["type"] = "object" }, OutputSchema = output });
        var node = graph.Workflows[0].Steps[0]; node.Type = "mcp.call"; node.CapabilityId = "cap"; node.Input = Obj(("model", Str("fake")));
        node.OutputSchema = new() { CapabilityId = "cap", SchemaPointer = "/output" };
        node.StructuredOutput = new(ObjectSchema(("message", "integer")));
        graph.Workflows[0].Outputs.Add(new() { Name = "structured", Schema = new() { Type = "integer" }, Value = new() { Kind = "output", Source = "greeting", ResultChannel = "structured", Path = ["message"] } });
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed-host", new() { Tools = [new() { Name = "renamed-method", InputSchema = preparation.Capabilities[0].InputSchema, OutputSchema = output }], ToolHandlers = new() { ["renamed-method"] = _ => new McpCallResult { Content = new JsonObject { ["message"] = "raw" } } } });
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        Assert.DoesNotContain("output_schema", yaml);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { McpClientFactory = factory, LLMClient = new JsonClient() }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("raw", result.Outputs?["message"]?.GetValue<string>()); Assert.Equal(42, result.Outputs?["structured"]?.GetValue<int>());
        node.OnError = [new(null, "continue", Obj(("message", new() { Kind = "number", Number = 42 })), null)];
        Assert.Contains(PlanningGraphValidation.Validate(graph, preparation), d => d.Code == "STRUCTURED_FALLBACK_INVALID");
    }

    [Fact]
    public void AcceptedBehaviorCannotBeSilentlyGuardedOrHaveOutcomesRemoved()
    {
        var plan = BehaviorPlan(); var graph = Graph();
        graph.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        Assert.Contains(PlanningBehaviorPlans.ValidateImplementation(plan, graph, Preparation()), d => d.Code == "BEHAVIOR_IMPLEMENTATION_CHANGED");
    }

    private static async Task<RunResult> Execute(PlanningGraph graph, PlanningPreparation preparation)
    {
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        return await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
    }
    private sealed class JsonClient : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => Task.FromResult(new LLMResponse { Json = new JsonObject { ["message"] = 42 } });
    }
}
