using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Authoring.JavaScript;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class WholeWorkflowConstructionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedWorkflowPlanner Planner() => new(sourceCompiler: new JavaScriptPlanningSourceCompiler());
    private static PlanningSnapshot Ready(string strategy)
    {
        var state = Session(PlanningStatus.Generating);
        state.Request.ConstructionStrategy = strategy; state.Request.Generation.MaxInputTokensPerUnit = 32_000;
        state.BehaviorPlan = BehaviorPlan(); state.Preparation = Preparation();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation);
        return state;
    }
    private static Task<PlanningSnapshot> Advance(PlanningSnapshot state, IPlanningRuntime runtime, string kind = "advance") =>
        Planner().AdvanceAsync(state, new() { Kind = kind, ExpectedRevision = state.Revision }, runtime, Ct);
    private static PlanningSnapshot Clone(PlanningSnapshot state) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;

    private static FakeRuntime Runtime(PlanningSnapshot seed, Func<int, string>? source = null, Func<int, IReadOnlyList<PlanningDiagnostic>>? validation = null)
    {
        var calls = 0;
        return new() { ValidationResult = validation, OnCall = (phase, request, _) =>
        {
            JsonObject response = new() { ["findings"] = new JsonArray() };
            if (phase is "typed_workflow" or "javascript_workflow")
            {
                Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
                var text = source?.Invoke(++calls) ?? JavaScriptConstructionTests.Source;
                response = phase == "javascript_workflow" ? new() { ["source"] = text } : PlanningModelValues.WholeWorkflow(
                    new JavaScriptPlanningSourceCompiler().Compile(text, new(seed.Graph!.Workflows[0], seed.Preparation!), Ct).Workflow!);
            }
            return Task.FromResult(new LLMResponse { Json = response });
        } };
    }

    [Theory]
    [InlineData(PlanningConstructionStrategies.TypedWorkflowsV1)]
    [InlineData(PlanningConstructionStrategies.JavaScriptV1)]
    public async Task ExecutableFailuresReturnToTheSameBoundedConstructionPath(string strategy)
    {
        var state = Ready(strategy);
        var runtime = Runtime(state, n => JavaScriptConstructionTests.Source.Replace("Hello", n == 1 ? "Hello" : "Repaired", StringComparison.Ordinal),
            n => n == 1 ? [new("CHECK_FAILED", "/workflows/0/steps/0", "Fix executable value")] : []);
        for (var i = 0; i < 12 && state.Status != PlanningStatus.FinalReview; i++) state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, Assert.Single(state.SourceCandidates).Calls); Assert.Equal(0, runtime.PreparationCalls);
        Assert.Contains("Repaired", state.Yaml); Assert.DoesNotContain(runtime.Phases, p => p.StartsWith("fragment", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PlanningConstructionStrategies.TypedWorkflowsV1)]
    [InlineData(PlanningConstructionStrategies.JavaScriptV1)]
    public async Task ConsumerCannotInventFieldsOfDeclaredProducer(string strategy)
    {
        var state = Ready(strategy);
        state.Preparation!.Capabilities.Add(new() { Id = "declared", StepType = "mcp.call", OperationIds = ["read"],
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"known":{"type":"string"}},"required":["known"],"additionalProperties":false}""")!.AsObject() });
        var workflow = state.BehaviorPlan!.Workflows[0]; workflow.OperationIds = ["read"];
        workflow.Steps = [new() { Key = "producer", Purpose = "Read declared data", CapabilityId = "declared", OperationIds = ["read"], InputDependencies = [] }];
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation);
        var runtime = Runtime(state, _ => """
            flow.workflow({steps:[flow.step("producer","mcp.call",{request:{}})],
              outputs:[flow.result("message",{type:"string"},flow.ref("producer","invented"))]});
            """);
        state = await Advance(state, runtime);
        Assert.Equal("invalid", Assert.Single(state.SourceCandidates).Status);
        Assert.Contains(state.Diagnostics, d => d.Required && d.Location.Contains("outputs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedTransportCoversEverySdkReferenceAndNativeContainer()
    {
        var state = Ready(PlanningConstructionStrategies.TypedWorkflowsV1); var runtime = Runtime(state);
        await Advance(state, runtime);
        var schema = runtime.Requests[0].StructuredOutputSchema!.AsObject();
        var authored = new JavaScriptPlanningSourceCompiler().Compile("""
            flow.workflow({steps:[flow.loop("loop",flow.input("items"),[
              flow.step("shape","set",{item:flow.item("loop"),index:flow.index("loop"),previous:flow.previous("loop","shape")}),
              flow.parallel("parallel",[[flow.step("a","set",{a:true})],[flow.step("b","set",{b:true})]])]),
              flow.when("guard",flow.expr("true"),[flow.choose("choice",flow.expr("1"),[{value:"1",steps:[]}])])],
              finally:[flow.step("cleanup","set",{result:flow.compute({value:flow.input("value")},"return value;"),
                collected:flow.artifactCollection("loop","shape","response","records")})]});
            """, new(new() { Key = "main" }, state.Preparation!), Ct);
        Assert.Empty(authored.Diagnostics);
        // Extend only the admitted native kinds for this transport algebra check.
        schema["$defs"]!["node"]!["properties"]!["type"]!["enum"] = new JsonArray("set", "loop.sequential", "parallel", "sequence", "switch");
        Assert.Empty(PlanningContractValidation.ValidateInstance(PlanningModelValues.WholeWorkflow(authored.Workflow!), schema));
    }

    [Fact]
    public async Task BothFormatsProduceIdenticalNativeYamlAndExecution()
    {
        var yaml = new List<string>();
        foreach (var strategy in new[] { PlanningConstructionStrategies.TypedWorkflowsV1, PlanningConstructionStrategies.JavaScriptV1 })
        {
            var state = Ready(strategy); var runtime = Runtime(state); var hash = state.ApprovedBehaviorHash;
            for (var i = 0; i < 10 && state.Status != PlanningStatus.FinalReview; i++) state = await Advance(state, runtime);
            Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(hash, state.ApprovedBehaviorHash);
            Assert.Null(state.ApprovedHash); Assert.Empty(state.ConstructionUnits);
            Assert.Equal(strategy, Assert.Single(state.SourceCandidates).Format);
            Assert.Equal(1, state.SourceCandidates[0].Calls); yaml.Add(state.Yaml!);
            var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
            var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
            Assert.True(result.Success, result.Error?.Message); Assert.Equal("Hello", result.Outputs!["message"]!.ToString());
        }
        Assert.Equal(yaml[0], yaml[1]);
    }

    [Theory]
    [InlineData(PlanningConstructionStrategies.TypedWorkflowsV1)]
    [InlineData(PlanningConstructionStrategies.JavaScriptV1)]
    public async Task LifetimeRepairAllowanceSurvivesRetryAndRestart(string strategy)
    {
        var state = Ready(strategy);
        var runtime = Runtime(state, n => JavaScriptConstructionTests.Source.Replace("Hello", "attempt" + n, StringComparison.Ordinal)
            .Replace("flow.ref(\"greeting\",\"message\")", "flow.ref(\"missing\",\"message\")", StringComparison.Ordinal));
        for (var i = 0; i < 3; i++) state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status); Assert.EndsWith("REPAIR_EXHAUSTED", state.Diagnostics.Last().Code);
        state = await Advance(Clone(state), runtime, "retry"); state = await Advance(state, runtime);
        Assert.Equal(3, runtime.Requests.Count); Assert.Equal(3, Assert.Single(state.SourceCandidates).Calls);
    }

    [Theory]
    [InlineData(PlanningConstructionStrategies.TypedWorkflowsV1)]
    [InlineData(PlanningConstructionStrategies.JavaScriptV1)]
    public async Task RepeatedFindingsStopImmediatelyAndFinalizersRemainLocked(string strategy)
    {
        var state = Ready(strategy);
        state.BehaviorPlan!.Workflows[0].Finally.Add(new() { Key = "cleanup", Purpose = "Always clean up", InputDependencies = [] });
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation!);
        var runtime = Runtime(state);
        state = await Advance(state, runtime); state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "BEHAVIOR_IMPLEMENTATION_CHANGED");
        Assert.Equal("cleanup", Assert.Single(state.Graph!.Workflows[0].Finally).Key);
        Assert.Equal(2, runtime.Requests.Count); Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData(PlanningConstructionStrategies.TypedWorkflowsV1)]
    [InlineData(PlanningConstructionStrategies.JavaScriptV1)]
    public async Task ContextCeilingIsMeasuredBeforeDispatchAndCanAdmitLargerPrompts(string strategy)
    {
        var state = Ready(strategy); state.Request.Prompt += new string('a', 45_000);
        var runtime = Runtime(state);
        state = await Advance(state, runtime);
        var candidate = Assert.Single(state.SourceCandidates);
        Assert.InRange(candidate.EstimatedInputTokens!.Value, 12_001, 32_000);
        Assert.Equal(32_000, candidate.InputTokenLimit); Assert.Single(runtime.Requests);
        var oversized = Ready(strategy); oversized.Request.Prompt += new string('a', 100_000);
        var unused = Runtime(oversized); oversized = await Advance(oversized, unused);
        Assert.Empty(unused.Requests); Assert.Contains(oversized.Diagnostics, d => d.Code.EndsWith("CONTEXT_TOO_LARGE", StringComparison.Ordinal));
        Assert.Equal(12_000, new PlanningGenerationOptions().MaxInputTokensPerUnit);
    }

    [Theory]
    [InlineData(PlanningConstructionStrategies.TypedWorkflowsV1)]
    [InlineData(PlanningConstructionStrategies.JavaScriptV1)]
    public async Task PendingRequestSchemaAndRevisionSurviveRestart(string strategy)
    {
        var state = Ready(strategy); PlanningSnapshot? checkpoint = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var interrupted = new FakeRuntime { OnCheckpoint = s => { checkpoint = Clone(s); cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Planner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, interrupted, cancellation.Token));
        var candidate = Assert.Single(checkpoint!.SourceCandidates); var originalRevision = candidate.PendingRevision;
        var prompt = candidate.PendingPrompt; var schema = candidate.PendingSchema!.ToJsonString();
        checkpoint.Revision += 5; var resumed = Runtime(checkpoint);
        state = await Advance(checkpoint, resumed);
        Assert.Equal(prompt, Assert.Single(resumed.Requests).Prompt);
        Assert.Equal(schema, resumed.Requests[0].StructuredOutputSchema!.ToJsonString());
        Assert.NotNull(originalRevision); Assert.Equal(1, Assert.Single(state.SourceCandidates).Calls);
    }

    [Theory]
    [InlineData(PlanningConstructionStrategies.TypedWorkflowsV1)]
    [InlineData(PlanningConstructionStrategies.JavaScriptV1)]
    public async Task CalleeFirstAndDependencyInvalidationDoNotResetCalls(string strategy)
    {
        var state = Ready(strategy); var producer = state.BehaviorPlan!.Workflows[0]; producer.Key = "producer";
        state.BehaviorPlan.Workflows.Insert(0, new() { Key = "main", Purpose = "Call producer", Outputs = [new("message", "Message", true)],
            Steps = [new() { Key = "invoke", Kind = "workflow", Purpose = "Return result", WorkflowKey = "producer", InputDependencies = [] }] });
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation!);
        var producerSeed = Clone(state); producerSeed.Graph!.Workflows.Reverse();
        var runtime = Runtime(producerSeed); state = await Advance(state, runtime);
        Assert.Equal("validated", state.SourceCandidates.Single(c => c.WorkflowKey == "producer").Status);
        Assert.Equal(0, state.SourceCandidates.Single(c => c.WorkflowKey == "main").Calls);
        state.SourceCandidates.Single(c => c.WorkflowKey == "producer").DependencyFingerprint = "invalidated";
        state = await Advance(Clone(state), runtime);
        Assert.Equal(2, state.SourceCandidates.Single(c => c.WorkflowKey == "producer").Calls);
        Assert.Equal(0, state.SourceCandidates.Single(c => c.WorkflowKey == "main").Calls);
    }
}
