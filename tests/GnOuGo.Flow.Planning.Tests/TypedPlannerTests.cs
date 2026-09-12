using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TypedPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningPreparation Preparation() => new() { Fingerprint = "catalog-v1", AllowedStepTypes = ["set", "emit", "switch", "sequence", "parallel", "loop.sequential", "workflow.call", "human.input", "mcp.call"] };
    internal static PlanningGraph Graph() => new()
    {
        Summary = "Return a greeting",
        Workflows = [new()
        {
            Key = "main", Purpose = "Return a greeting",
            Steps = [new() { Key = "greeting", Type = "set", Input = Obj(("message", Str("Hello"))) }],
            Outputs = [new() { Name = "message", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "greeting", Path = ["message"] } }]
        }]
    };
    internal static PlanningBehaviorPlan BehaviorPlan() => CompleteInputDependencies(new()
    {
        Summary = "Return a greeting",
        Workflows = [new()
        {
            Key = "main", Purpose = "Return a greeting", Outputs = [new("message", "The greeting", true)],
            Steps = [new() { Key = "greeting", Purpose = "Return a greeting" }]
        }]
    });

    private static PlanningBehaviorPlan CompleteInputDependencies(PlanningBehaviorPlan plan)
    {
        foreach (var node in plan.Workflows.SelectMany(w => PlanningBehaviorPlans.Enumerate(w.Steps.Concat(w.Finally)))) node.InputDependencies ??= [];
        return plan;
    }
    internal static PlanningValue Str(string text) => new() { Kind = "string", Text = text };
    internal static PlanningValue Obj(params (string Key, PlanningValue Value)[] members) => new() { Kind = "object", Members = members.Select(m => new PlanningMember(m.Key, m.Value)).ToList() };
    internal static PlanningSnapshot Session(string status = PlanningStatus.Created) => new() { Request = new() { TenantId = "tenant", Prompt = "Return a greeting", MaxRepairsPerWorkflowGate = 1 }, Status = status };
    private static Task<PlanningSnapshot> Send(IWorkflowPlanner planner, PlanningSnapshot state, IPlanningRuntime runtime, string kind = "advance", string? text = null)
        => planner.AdvanceAsync(state, new() { Kind = kind, ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash, Text = text }, runtime, Ct);

    [Fact]
    public async Task DeterministicExport_CompilesAndExecutesWithoutPlanningMetadata()
    {
        var graph = Graph();
        graph.Workflows[0].Steps[0].Purpose = "PRIVATE PLANNING EVIDENCE";
        var compiler = new PlanningGraphCompiler();
        var yaml = compiler.Compile(graph, Preparation());
        Assert.Equal(yaml, compiler.Compile(graph, Preparation()));
        Assert.DoesNotContain("PRIVATE PLANNING EVIDENCE", yaml);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Hello", result.Outputs?["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task FullSession_PausesBeforeElaboration_AndBindsApprovalToExactRevision()
    {
        var planner = new TypedWorkflowPlanner();
        var runtime = new FakeRuntime();
        var state = Session();
        for (var i = 0; i < 3; i++) state = await Send(planner, state, runtime);
        Assert.True(state.Status == PlanningStatus.BehaviorReview, state.Status + ": " + string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("fragment", runtime.Phases);
        var review = state;
        state = await Send(planner, state, runtime, "accept_behavior");
        for (var i = 0; i < 15 && state.Status is PlanningStatus.Generating or PlanningStatus.Validating; i++) state = await Send(planner, state, runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.NotNull(state.Yaml);
        Assert.Equal(PlanningGraphCompiler.Fingerprint(state.Yaml), state.ArtifactHash);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = review.Revision, ArtifactHash = review.ArtifactHash }, runtime, Ct));
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = "stale" }, runtime, Ct));
        state = await Send(planner, state, runtime, "approve");
        Assert.Equal(PlanningStatus.Approved, state.Status);
        Assert.Equal(state.ArtifactHash, state.ApprovedHash);
        Assert.Equal(2, runtime.ValidationCalls);
    }

    [Fact]
    public async Task InvalidStructuredOutput_IsRetriedOnceWithIdenticalSchema_AndFailsClosed()
    {
        var runtime = new FakeRuntime { InvalidJson = true };
        var state = await Send(new TypedWorkflowPlanner(), Session(), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.True(JsonNode.DeepEquals(runtime.Requests[0].StructuredOutputSchema, runtime.Requests[1].StructuredOutputSchema));
        Assert.Null(state.Yaml);
    }

    [Theory]
    [InlineData(PlanningStatus.Created, "intent")]

    public async Task ProviderFailurePausesWithAnActionableFindingAndRetainsTheSession(string status, string phase)
    {
        var state = Session(status);
        if (status == PlanningStatus.Generating) { state.Graph = Graph(); state.Preparation = Preparation(); }
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new LLMClientException(LLMClientFailureKind.Transport, "The provider could not be reached.", true, 503, "upstream_unavailable") };
        var result = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Equal(phase, result.CurrentPhase);
        Assert.Equal(state.Request.SessionId, result.Request.SessionId);
        Assert.Equal(state.Graph is null, result.Graph is null);
        Assert.Null(result.Intent.Question); Assert.Null(result.Outcome);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("LLM_PROVIDER_TRANSPORT", diagnostic.Code);
        Assert.Contains("503", diagnostic.Message); Assert.Contains("upstream_unavailable", diagnostic.Message);
        Assert.Single(runtime.Requests);
    }

    [Fact]
    public async Task InconclusiveRequiredScenario_BlocksFinalReview()
    {
        var runtime = new FakeRuntime { ScenarioOutcome = "inconclusive" };
        var state = Session(PlanningStatus.Validating);
        state.Graph = Graph(); state.Preparation = Preparation(); state.Request.MaxRepairsPerWorkflowGate = 0; PlanningFixtures.Accept(state);
        state = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(PlanningPhase.Repair, state.CurrentPhase);
        Assert.Contains(state.Diagnostics, d => d.Code == "SCENARIO_INCONCLUSIVE");
        Assert.DoesNotContain("semantic_review", runtime.Phases);
    }

    [Fact]
    public async Task RestartAtCheckpoint_DoesNotRepeatCompletedPhases()
    {
        var runtime = new FakeRuntime();
        var state = await Send(new TypedWorkflowPlanner(), Session(), runtime);
        state = await Send(new TypedWorkflowPlanner(), state, runtime);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.True(state.Status == PlanningStatus.BehaviorReview, state.Status + ": " + string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        Assert.Equal(1, runtime.Phases.Count(p => p == "intent"));
        Assert.Equal(1, runtime.PreparationCalls);
    }

    [Fact]
    public async Task HumanWaiting_IsSeparateFromActiveTime()
    {
        var time = new FixedClock();
        var state = Session(PlanningStatus.BehaviorReview);
        state.Preparation = Preparation(); state.BehaviorPlan = BehaviorPlan(); state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.WaitingSinceUtc = time.GetUtcNow().AddHours(-2);
        state = await Send(new TypedWorkflowPlanner(time), state, new FakeRuntime(), "accept_behavior");
        Assert.Equal(7_200_000, state.HumanWaitMilliseconds);
        Assert.InRange(state.ActiveMilliseconds, 0, 10_000);
    }

    [Fact]
    public void MissingProducer_WeakArray_AndUnboundExternalCall_AreRejected()
    {
        var compiler = new PlanningGraphCompiler();
        var graph = Graph(); graph.Workflows[0].Outputs[0].Value.Source = "missing";
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(graph, Preparation()));
        graph = Graph(); graph.Workflows[0].Outputs[0].Schema = new() { Type = "array" };
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(graph, Preparation()));
        graph = Graph(); graph.Workflows[0].Steps[0].Type = "mcp.call";
        Assert.Throws<InvalidOperationException>(() => compiler.Compile(graph, Preparation()));
    }

    [Fact]
    public void AuthoritativeSchema_IsCopiedExactly_AndUnsupportedPortConstraintsFailClosed()
    {
        var preparation = Preparation();
        var output = JsonNode.Parse("""{"type":"string","minLength":2}""")!.AsObject();
        preparation.Capabilities.Add(new() { Id = "c", OutputSchema = output });
        var reference = new PlanningSchema { CapabilityId = "c", SchemaPointer = "/output" };
        Assert.True(JsonNode.DeepEquals(output, PlanningGraphCompiler.ToJsonSchema(reference, preparation)));
        var graph = Graph(); graph.Workflows[0].Outputs[0].Schema = reference;
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, preparation));
    }

    [Fact]
    public void ImportRejectsUnsupportedConstructsRatherThanDroppingThem()
    {
        var yaml = new PlanningGraphCompiler().Compile(Graph(), Preparation());
        Assert.Throws<InvalidOperationException>(() => PlanningGraphImporter.Import(yaml + "meta: {hidden: true}\n", Preparation()));
    }

    [Fact]
    public async Task LegacyPendingQuestionRequiresReassessmentOnExplicitInteraction()
    {
        var state = Session(PlanningStatus.Clarification);
        state.Intent.Question = new() { StepId = "question", Prompt = "Clarify the behavior", Fields = [new() { Name = "behavior_0", Description = "Should approval be required before an external write?", Type = "text", Required = true, AllowCustomAnswer = true }] };
        state.Outcome = new PlanningNeedUserClarification(new("behavior_0", [], PlanningHoleRequests.Object(("behavior_0", PlanningHoleRequests.Type("string"))), ["behavior"]));
        var next = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new JsonObject { ["behavior_0"] = "yes" } }, new FakeRuntime(), Ct);
        Assert.Equal(PlanningStatus.Created, next.Status);
        Assert.Empty(next.Intent.Answers); Assert.Null(next.Intent.Question); Assert.Null(next.Outcome);
    }

    [Fact]
    public void ReviewShowsSkippedActionsAndChangesToExistingBoundarySchemas()
    {
        var before = Graph();
        var after = Graph();
        after.Workflows[0].Outputs[0].Schema.Nullable = true;
        after.Workflows[0].Steps[0].If = new() { Kind = "expression", Text = "inputs.approved" };
        Assert.Contains(PlanningReviewFormatter.Diff(before, after), change => change.StartsWith("Input or output contract changed:", StringComparison.Ordinal));
        var diagram = PlanningReviewFormatter.Diagram(after, Preparation(), before);
        Assert.Contains("If inputs.approved", diagram);
        Assert.Contains("-->|No action|", diagram);
        Assert.Contains("stroke-width:3px", diagram);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    }

    internal sealed class FakeRuntime : IPlanningRuntime
    {
        private PlanningSnapshot? _state;
        public List<string> Phases { get; } = [];
        public List<LLMRequest> Requests { get; } = [];
        public bool InvalidJson { get; init; }
        public string ScenarioOutcome { get; init; } = "passed";
        public int ValidationCalls { get; private set; }
        public int ScenarioCalls { get; private set; }
        public int PreparationCalls { get; private set; }
        public int CatalogCalls { get; private set; }
        public IReadOnlyList<PlanningDiagnostic> CatalogDiagnostics { get; init; } = [];
        public Func<string, LLMRequest, CancellationToken, Task<LLMResponse>>? OnCall { get; set; }
        public Func<int, IReadOnlyList<PlanningDiagnostic>>? ValidationResult { get; init; }
        public Func<PlanningRequest, Task<PlanningPreparation>>? OnPrepare { get; init; }
        public Func<PlanningSnapshot, Task<PlanningPreparation>>? OnPrepareSnapshot { get; set; }
        public Func<PlanningSnapshot, Task>? OnCheckpoint { get; set; }
        public Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct) { _state = snapshot; return OnCheckpoint?.Invoke(snapshot) ?? Task.CompletedTask; }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningPreparation preparation, CancellationToken ct)
        { CatalogCalls++; return Task.FromResult(CatalogDiagnostics); }
        public async Task<PlanningPreparationProgress> PrepareAsync(PlanningSnapshot state, CancellationToken ct) { PreparationCalls++; return new(state.PreparationCheckpoint ?? new(), OnPrepareSnapshot is not null ? await OnPrepareSnapshot(state) : OnPrepare is null ? PreparedLocal(state) : await OnPrepare(state.Request)); }
        private static PlanningPreparation PreparedLocal(PlanningSnapshot state)
        {
            var preparation = Preparation();
            preparation.Capabilities.Add(new() { Id = "local_greeting", StepType = "set", Resolution = "available", Description = "Return a greeting", Required = true,
                OperationIds = state.Obligations.Where(o => o.Kind == "local_processing").Select(o => o.Id).DefaultIfEmpty("greeting").ToList(),
                FixedInput = new() { ["message"] = "Hello" }, OutputSchema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["message"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("message"), ["additionalProperties"] = false } });
            return preparation;
        }
        internal static JsonObject Interpret(LLMRequest request, string kind = "local_processing") => new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
        {
            JsonObject Item(string role) => new() { ["kind"] = role, ["required"] = true,
                ["start"] = p.Value!["items"]!["properties"]!["start"]!["enum"]![0]!.DeepClone(),
                ["end"] = p.Value!["items"]!["properties"]!["end"]!["enum"]!.AsArray()[^1]!.DeepClone() };
            var items = new JsonArray(Item(kind));
            if (kind == "local_processing") items.Add((JsonNode)Item("business_output"));
            return new KeyValuePair<string, JsonNode?>(p.Key, items);
        }));
        internal static JsonObject PassReview(LLMRequest request) => new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
            new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["status"] = "passed" })));
        internal static JsonObject NewSchema(LLMRequest request) => new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
            new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["properties"]?["type"] is not null ? new JsonObject { ["type"] = "string", ["nullable"] = false }
                : new JsonObject { ["members"] = new JsonArray(new JsonObject { ["name"] = "message", ["required"] = true }), ["more"] = false })));
        public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
        {
            lock (Phases) { Phases.Add(phase); Requests.Add(request); }
            if (OnCall is not null) return OnCall(phase, request, ct);
            JsonNode? json = InvalidJson ? new JsonObject() : phase switch
            {
                "intent" => Interpret(request),
                "behavior" => JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan),
                "construction" => FillHoles(request, new PlanningGraph { Workflows = [ExecutableWorkflow()] }),
                "semantic_review" => PassReview(request),
                "construction_schema" => NewSchema(request),
                "scenario_inputs" => new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("fixture")))),
                _ => throw new InvalidOperationException("Unexpected model phase: " + phase)
            };
            return Task.FromResult(new LLMResponse { Json = json, Text = json!.ToJsonString() });
        }
        internal static PlanningWorkflow ExecutableWorkflow()
        {
            var workflow = Graph().Workflows[0];
            workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
            return workflow;
        }
        internal JsonObject FillHoles(LLMRequest request, PlanningGraph fixture)
        {
            var state = _state ?? throw new InvalidOperationException("Requests must be checkpointed before dispatch.");
            var json = PlanningFieldPaths.Json(fixture); var assignments = new JsonObject();
            foreach (var id in request.StructuredOutputSchema!["properties"]!["assignments"]!["properties"]!.AsObject().Select(p => p.Key))
            {
                var hole = state.Construction.Holes.Single(h => h.Id == id);
                var value = PlanningFieldPaths.Read(json, hole.Path);
                if (hole.Kind == "schema") assignments[id] = PlanningModelValues.Compact(value);
                else if (value is null) assignments[id] = new JsonObject { ["kind"] = "absent" };
                else
                {
                    var typed = JsonSerializer.Deserialize(value, PlanningJsonContext.Default.PlanningValue)!;
                    if (typed.Kind == "output" && fixture.Workflows[0].Steps.FindIndex(n => n.Key == typed.Source) is var sourceIndex && sourceIndex >= 0)
                        typed.Source = state.Graph!.Workflows.Single(w => w.Key == hole.WorkflowKey).Steps[sourceIndex].Key;
                    if (PlanningGraphValidation.IsLiteral(typed)) assignments[id] = new JsonObject { ["kind"] = "literal", ["json"] = PlanningGraphValidation.Literal(typed) };
                    else
                    {
                        var owner = state.Graph!.Workflows.Single(w => w.Key == hole.WorkflowKey);
                        var binding = PlanningHoleRequests.Catalog(state, owner, hole).Single(b => b.Id == PlanningBindingIdentity.Id(typed));
                        assignments[id] = new JsonObject { ["kind"] = "binding", ["binding"] = "p_" + binding.Id[2..14] };
                    }
                }
            }
            return new() { ["assignments"] = assignments };
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct)
        {
            ValidationCalls++; new WorkflowCompiler().Compile(WorkflowParser.Parse(request.Yaml));
            return Task.FromResult(ValidationResult?.Invoke(ValidationCalls) ?? (IReadOnlyList<PlanningDiagnostic>)[]);
        }
        public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(PlanningScenarioValidationRequest request, CancellationToken ct)
        {
            ScenarioCalls++;
            return Task.FromResult<IReadOnlyList<PlanningScenarioResult>>([new("nominal", ScenarioOutcome, "Fake integration scenario", [])]);
        }
    }
}
