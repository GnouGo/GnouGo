using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Authoring.JavaScript;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class JavaScriptConstructionTests
{
    internal const string Source = """
        flow.workflow({steps:[flow.step("greeting","set",{message:"Hello"},
          {outputSchema:{type:"object",properties:[{name:"message",schema:{type:"string"},required:true}]}})],
          outputs:[flow.result("message",{type:"string"},flow.ref("greeting","message"))]});
        """;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedWorkflowPlanner Planner() => new(sourceCompiler: new JavaScriptPlanningSourceCompiler());
    private static PlanningSnapshot Ready()
    {
        var state = Session(PlanningStatus.Generating);
        state.Request.ConstructionStrategy = PlanningConstructionStrategies.JavaScriptV1;
        state.BehaviorPlan = BehaviorPlan(); state.Preparation = Preparation();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation);
        return state;
    }
    private static Task<PlanningSnapshot> Advance(TypedWorkflowPlanner planner, PlanningSnapshot state, IPlanningRuntime runtime, string kind = "advance") =>
        planner.AdvanceAsync(state, new() { Kind = kind, ExpectedRevision = state.Revision }, runtime, Ct);
    private static FakeRuntime Runtime(Func<int, string>? source = null, Func<int, IReadOnlyList<PlanningDiagnostic>>? validation = null)
    {
        var calls = 0;
        return new() { ValidationResult = validation, OnCall = (phase, _, _) => Task.FromResult(new LLMResponse
        { Json = phase == "javascript_workflow" ? new JsonObject { ["source"] = source?.Invoke(++calls) ?? Source } : new JsonObject { ["findings"] = new JsonArray() } }) };
    }

    [Fact]
    public async Task GeneratesNativeYamlThroughExistingValidationAndExactApprovalGate()
    {
        var state = Ready(); var approved = state.ApprovedBehaviorHash; var runtime = Runtime(); var planner = Planner();
        for (var i = 0; i < 8 && state.Status != PlanningStatus.FinalReview; i++) state = await Advance(planner, state, runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(approved, state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash);
        Assert.Equal("set", GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!).Workflows["main"].Steps[0].Type);
        Assert.DoesNotContain("flow.workflow", state.Yaml);
        Assert.Equal(1, Assert.Single(state.SourceCandidates).Calls);
        Assert.True(runtime.ValidationCalls > 0); Assert.True(runtime.ScenarioCalls > 0);
        Assert.DoesNotContain(runtime.Phases, p => p.StartsWith("fragment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedInvalidCandidateStopsWithoutThirdDispatch()
    {
        var runtime = Runtime(_ => Source.Replace("flow.ref(\"greeting\",\"message\")", "flow.ref(\"missing\",\"message\")", StringComparison.Ordinal));
        var planner = Planner(); var state = await Advance(planner, Ready(), runtime);
        state = await Advance(planner, state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "JS_REPAIR_REPEATED");
        state = await Advance(planner, state, runtime, "retry");
        state = await Advance(planner, state, runtime);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.Equal(2, Assert.Single(state.SourceCandidates).Calls);
    }

    [Fact]
    public async Task ThreeDifferentInvalidCandidatesExhaustLifetimeAllowanceEvenAfterRetry()
    {
        var runtime = Runtime(n => Source.Replace("Hello", "attempt" + n, StringComparison.Ordinal).Replace("flow.ref(\"greeting\",\"message\")", "flow.ref(\"missing\",\"message\")", StringComparison.Ordinal));
        var planner = Planner(); var state = Ready();
        for (var i = 0; i < 3; i++) state = await Advance(planner, state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "JS_REPAIR_EXHAUSTED");
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await Advance(Planner(), state, runtime, "retry");
        state = await Advance(Planner(), state, runtime);
        Assert.Equal(3, runtime.Requests.Count);
        Assert.Equal(3, Assert.Single(state.SourceCandidates).Calls);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task RuntimeFindingReturnsToSourceRepairWithoutFragmentOrPreparationLoop()
    {
        var runtime = Runtime(n => Source.Replace("Hello", n == 1 ? "Hello" : "Welcome", StringComparison.Ordinal),
            n => n == 1 ? [new("CHECK_FAILED", "/workflows/0/steps/0", "Repair the generated message")] : []);
        var state = Ready(); var planner = Planner();
        for (var i = 0; i < 12 && state.Status != PlanningStatus.FinalReview; i++) state = await Advance(planner, state, runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, Assert.Single(state.SourceCandidates).Calls);
        Assert.Contains("Welcome", state.Yaml);
        Assert.Equal(0, runtime.PreparationCalls);
    }

    [Fact]
    public async Task CalleeContractIsBuiltBeforeCaller()
    {
        var state = Ready();
        var producer = state.BehaviorPlan!.Workflows[0]; producer.Key = "producer";
        state.BehaviorPlan.Workflows.Insert(0, new() { Key = "main", Purpose = "Call producer", Outputs = [new("message", "Message", true)],
            Steps = [new() { Key = "invoke", Kind = "workflow", Purpose = "Return the producer result", WorkflowKey = "producer", InputDependencies = [] }] });
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation);
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        var runtime = Runtime(); state = await Advance(Planner(), state, runtime);
        Assert.True(state.SourceCandidates.Count > 0, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal("validated", state.SourceCandidates.Single(c => c.WorkflowKey == "producer").Status);
        Assert.Equal(0, state.SourceCandidates.Single(c => c.WorkflowKey == "main").Calls);
    }

    [Fact]
    public async Task PendingRequestSurvivesCheckpointRestartWithoutConsumingAnotherAttempt()
    {
        PlanningSnapshot? checkpoint = null;
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runtime = new FakeRuntime { OnCheckpoint = s =>
        {
            checkpoint = JsonSerializer.Deserialize(JsonSerializer.Serialize(s, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot);
            interrupted.Cancel();
            throw new OperationCanceledException(interrupted.Token);
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Planner().AdvanceAsync(Ready(), new(), runtime, interrupted.Token));
        Assert.NotNull(checkpoint);
        var pending = Assert.Single(checkpoint.SourceCandidates);
        Assert.Equal(1, pending.Calls); Assert.NotNull(pending.PendingPrompt);
        checkpoint.Revision += 3;
        var resumed = Runtime();
        var state = await Advance(Planner(), checkpoint, resumed);
        Assert.Equal(pending.PendingPrompt, Assert.Single(resumed.Requests).Prompt);
        Assert.Equal(1, Assert.Single(state.SourceCandidates).Calls);
        Assert.Null(state.SourceCandidates[0].PendingPrompt);
    }

    [Fact]
    public async Task OversizedContractStopsBeforeDispatch()
    {
        var state = Ready(); state.Request.Generation.MaxInputTokensPerUnit = 512;
        var runtime = Runtime(); state = await Advance(Planner(), state, runtime);
        Assert.Contains(state.Diagnostics, d => d.Code == "JS_CONTEXT_TOO_LARGE");
        Assert.Empty(runtime.Requests);
        var unit = Assert.Single(state.SourceCandidates);
        Assert.True(unit.EstimatedInputTokens > unit.InputTokenLimit);
        Assert.Equal("context_limited", unit.Status);
        Assert.Equal(0, unit.Calls);
    }

    [Fact]
    public void OldSnapshotsKeepTypedUnitsStrategy()
    {
        var state = JsonSerializer.Deserialize("{\"request\":{\"tenantId\":\"t\",\"prompt\":\"p\"}}", PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal(PlanningConstructionStrategies.TypedUnitsV2, state.Request.ConstructionStrategy);
    }

    [Fact]
    public async Task AuthoringCannotRemoveAcceptedFinalizer()
    {
        var state = Ready();
        state.BehaviorPlan!.Workflows[0].Finally.Add(new() { Key = "cleanup", Purpose = "Always clean up", InputDependencies = [] });
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation);
        state = await Advance(Planner(), state, Runtime());
        Assert.Contains(state.Diagnostics, d => d.Code == "BEHAVIOR_IMPLEMENTATION_CHANGED");
        Assert.Equal("cleanup", Assert.Single(state.Graph!.Workflows[0].Finally).Key);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public void DeclaredProducerSchemaRejectsInventedConsumerField()
    {
        var template = new PlanningWorkflow { Key = "main", OperationIds = ["read"],
            Steps = [new() { Key = "producer", Type = "mcp.call", CapabilityId = "declared", OperationIds = ["read"] }] };
        var preparation = Preparation();
        preparation.Capabilities.Add(new() { Id = "declared", StepType = "mcp.call", OperationIds = ["read"],
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"known":{"type":"string"}},"required":["known"],"additionalProperties":false}""")!.AsObject() });
        var result = new JavaScriptPlanningSourceCompiler().Compile("""
            flow.workflow({steps:[flow.step("producer","mcp.call",{request:{}})],
              outputs:[flow.result("message",{type:"string"},flow.ref("producer","invented"))]});
            """, new(template, preparation), Ct);
        Assert.Empty(result.Diagnostics);
        var findings = PlanningGraphValidation.Validate(new() { Workflows = [result.Workflow!] }, preparation);
        Assert.Contains(findings, d => d.Required && d.Location.Contains("outputs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthoredWorkflowCallPassesRuntimeArgumentsAndExecutesNativeFinalizer()
    {
        var compiler = new JavaScriptPlanningSourceCompiler(); var preparation = Preparation();
        var child = compiler.Compile("""
            flow.workflow({inputs:[flow.port("name",{type:"string"})],steps:[
              flow.step("make","set",{message:flow.input("name")},{outputSchema:{type:"object",properties:[
                {name:"message",schema:{type:"string"},required:true}]}})],
              outputs:[flow.result("message",{type:"string"},flow.ref("make","message"))]});
            """, new(new() { Key = "child" }, preparation), Ct);
        var main = compiler.Compile("""
            flow.workflow({inputs:[flow.port("name",{type:"string"})],steps:[
              flow.call("invoke","child",{name:flow.input("name")})],
              outputs:[flow.result("message",{type:"string"},flow.ref("invoke","message"))],
              finally:[flow.step("cleanup","set",{cleaned:true})]});
            """, new(new() { Key = "main" }, preparation), Ct);
        Assert.Empty(child.Diagnostics); Assert.Empty(main.Diagnostics);
        var graph = new PlanningGraph { Workflows = [main.Workflow!, child.Workflow!] };
        var yaml = new PlanningGraphCompiler().Compile(graph, preparation);
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject { ["name"] = "runtime value" }, Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("runtime value", result.Outputs!["message"]!.ToString());
        Assert.Contains(result.StepResults, s => s.StepId == "n_" + PlanningGraphCompiler.Fingerprint("cleanup")[..16]);
    }
}
