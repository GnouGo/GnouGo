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
        Summary = "Return a greeting", Workflows = [new()
        {
            Key = "main", Purpose = "Return a greeting",
            Steps = [new() { Key = "greeting", Type = "set", Input = Obj(("message", Str("Hello"))) }],
            Outputs = [new() { Name = "message", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "greeting", Path = ["message"] } }]
        }]
    };
    internal static PlanningBehaviorPlan BehaviorPlan() => CompleteInputDependencies(new()
    {
        Summary = "Return a greeting", Workflows = [new()
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
    internal static PlanningSnapshot Session(string status = PlanningStatus.Created) => new() { Request = new() { TenantId = "tenant", Prompt = "Return a greeting", MaxRepairs = 1 }, Status = status };
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
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
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
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.True(JsonNode.DeepEquals(runtime.Requests[0].StructuredOutputSchema, runtime.Requests[1].StructuredOutputSchema));
        Assert.Null(state.Yaml);
    }

    [Theory]
    [InlineData(PlanningStatus.Created, "intent")]
    [InlineData(PlanningStatus.Generating, "fragment")]
    public async Task ProviderFailurePausesWithAnActionableFindingAndRetainsTheSession(string status, string phase)
    {
        var state = Session(status);
        if (status == PlanningStatus.Generating) { state.Graph = Graph(); state.Preparation = Preparation(); }
        var runtime = new FakeRuntime { OnCall = (_, _, _) => throw new LLMClientException(LLMClientFailureKind.Transport, "The provider could not be reached.", true, 503, "upstream_unavailable") };
        var result = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(PlanningStatus.Recovery, result.Status); Assert.Equal(phase, result.CurrentPhase);
        Assert.Equal(state.Request.SessionId, result.Request.SessionId);
        Assert.Equal(state.Graph is null, result.Graph is null);
        Assert.Null(result.Question); Assert.Null(result.Outcome);
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
        state.Graph = Graph(); state.Preparation = Preparation(); state.Request.MaxRepairs = 0;
        state = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "SCENARIO_INCONCLUSIVE");
        Assert.DoesNotContain("semantic_review", runtime.Phases);
    }

    [Fact]
    public async Task ManualYamlEdit_InvalidatesApproval_AndRunsCompleteValidationAgain()
    {
        var planner = new TypedWorkflowPlanner();
        var runtime = new FakeRuntime();
        var state = Session(PlanningStatus.Validating);
        state.Graph = Graph(); state.Preparation = Preparation();
        state = await Send(planner, state, runtime);
        state = await Send(planner, state, runtime, "approve");
        var oldHash = state.ArtifactHash;
        state = await Send(planner, state, runtime, "edit_yaml", state.Yaml!.Replace("Hello", "Welcome", StringComparison.Ordinal));
        Assert.Equal(PlanningStatus.Validating, state.Status);
        Assert.Null(state.ApprovedHash);
        state = await Send(planner, state, runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.NotEqual(oldHash, state.ArtifactHash);
        Assert.Equal(3, runtime.ValidationCalls);
        Assert.Equal(2, runtime.ScenarioCalls);
    }

    [Fact]
    public async Task FragmentCannotRemoveReviewedFinalizer()
    {
        var state = Session(PlanningStatus.Generating);
        state.Graph = Graph(); state.Preparation = Preparation();
        state.Graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Type = "set", Input = Obj(("closed", new() { Kind = "boolean", Boolean = true })) });
        var result = await Send(new TypedWorkflowPlanner(), state, new FakeRuntime());
        Assert.Equal(PlanningStatus.Recovery, result.Status);
        Assert.Single(result.Graph!.Workflows[0].Finally);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("reviewed control flow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartAtCheckpoint_DoesNotRepeatCompletedPhases()
    {
        var runtime = new FakeRuntime();
        var state = await Send(new TypedWorkflowPlanner(), Session(), runtime);
        state = await Send(new TypedWorkflowPlanner(), state, runtime);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        Assert.Equal(1, runtime.Phases.Count(p => p == "intent"));
        Assert.Equal(1, runtime.PreparationCalls);
    }

    [Fact]
    public async Task HumanWaiting_IsSeparateFromActiveTime()
    {
        var time = new FixedClock();
        var state = Session(PlanningStatus.BehaviorReview);
        state.Graph = Graph(); state.Preparation = Preparation(); state.ArtifactHash = "review";
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
    public async Task ExistingWorkflow_IsImportedBeforeModelWork_AndUnsupportedFieldsBlockRevision()
    {
        var yaml = new PlanningGraphCompiler().Compile(Graph(), Preparation());
        var state = Session(); state.Request.ExistingYaml = yaml;
        var runtime = new FakeRuntime();
        var next = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.NotNull(next.PreviousGraph);
        Assert.Contains("existing_workflow", runtime.Requests[0].Prompt);
        Assert.Contains("Hello", runtime.Requests[0].Prompt);
        state.Request.ExistingYaml = yaml + "meta: {hidden: true}\n";
        runtime = new FakeRuntime();
        next = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(PlanningStatus.Unsupported, next.Status);
        Assert.Contains(next.Diagnostics, d => d.Code == "IMPORT_UNSUPPORTED");
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task ClarificationRetainsTheQuestionMeaningWhenTheAnswerIsShort()
    {
        var state = Session(PlanningStatus.Clarification);
        state.Question = new() { StepId = "question", Prompt = "Clarify the behavior", Fields = [new() { Name = "behavior_0", Description = "Should approval be required before an external write?", Type = "text", Required = true, AllowCustomAnswer = true }] };
        var next = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new JsonObject { ["behavior_0"] = "yes" } }, new FakeRuntime(), Ct);
        Assert.Equal(PlanningStatus.Created, next.Status);
        Assert.Contains("Should approval be required before an external write?", Assert.Single(next.Answers).Question);
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

    [Fact]
    public async Task ElaborationUsesFourConcurrentCalls_AndResumesOnlyMissingFragments()
    {
        var state = Session(PlanningStatus.Generating);
        state.Preparation = Preparation();
        state.Graph = new() { Entrypoint = "w0", Workflows = Enumerable.Range(0, 8).Select(i => { var workflow = Graph().Workflows[0]; workflow.Key = "w" + i; return workflow; }).ToList() };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = async (_, _, ct) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            if (index == 3) entered.SetResult();
            await release.Task.WaitAsync(ct);
            var workflow = Graph().Workflows[0]; workflow.Key = "w" + index;
            return new() { Json = PlanningModelValues.Workflow(workflow) };
        } };
        var planner = new TypedWorkflowPlanner();
        var advance = Send(planner, state, runtime);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(4, calls);
        release.SetResult();
        state = await advance;
        Assert.Equal(4, state.Fragments.Count);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        state = await Send(new TypedWorkflowPlanner(), state, runtime);
        Assert.Equal(8, calls);
        Assert.Equal(8, state.Fragments.Count);
        Assert.Equal(PlanningStatus.Validating, state.Status);
    }

    [Fact]
    public async Task NonImprovingRepair_RestoresTheBestCandidate()
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        var runtime = new FakeRuntime
        {
            ValidationResult = count => [new(count == 1 ? "ORIGINAL_DEFECT" : "REGRESSION", "main", "A required check failed.")],
            OnCall = (_, _, _) =>
            {
                var input = new JsonObject { ["kind"] = "object", ["members"] = new JsonArray(new JsonObject { ["name"] = "message", ["value"] = new JsonObject { ["kind"] = "string", ["text"] = "Regressed" } }) };
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["workflow"] = "main", ["node"] = "greeting", ["field"] = "input", ["value"] = input }) } });
            }
        };
        var planner = new TypedWorkflowPlanner();
        state = await Send(planner, state, runtime);
        state = await Send(planner, state, runtime);
        state = await Send(planner, state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Equal("Hello", state.Graph!.Workflows[0].Steps[0].Input.Members[0].Value.Text);
        Assert.Contains(state.Diagnostics, d => d.Code == "ORIGINAL_DEFECT");
        Assert.Contains(state.Attempts, a => !a.Retained && a.Diagnostics.Any(d => d.Code == "REGRESSION"));
    }

    [Fact]
    public async Task NaturalLanguageRevision_InvalidatesPriorApproval()
    {
        var state = Session(PlanningStatus.Approved); state.Graph = Graph(); state.Preparation = Preparation();
        state.Yaml = new PlanningGraphCompiler().Compile(state.Graph, state.Preparation);
        state.ArtifactHash = state.ApprovedHash = PlanningGraphCompiler.Fingerprint(state.Yaml);
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse
        {
            Json = new JsonObject { ["affectedWorkflows"] = new JsonArray("main"), ["changesBehavior"] = false, ["evidence"] = "Change greeting wording" }
        }) };
        state = await Send(new TypedWorkflowPlanner(), state, runtime, "revise", "Change greeting wording");
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Null(state.ApprovedHash);
        Assert.Null(state.Yaml);
        Assert.NotNull(state.PreviousGraph);
        Assert.Contains("main", state.ChangedFragments);
    }

    internal sealed class FakeRuntime : IPlanningRuntime
    {
        public List<string> Phases { get; } = [];
        public List<LLMRequest> Requests { get; } = [];
        public bool InvalidJson { get; init; }
        public string ScenarioOutcome { get; init; } = "passed";
        public int ValidationCalls { get; private set; }
        public int ScenarioCalls { get; private set; }
        public int PreparationCalls { get; private set; }
        public Func<string, LLMRequest, CancellationToken, Task<LLMResponse>>? OnCall { get; init; }
        public Func<int, IReadOnlyList<PlanningDiagnostic>>? ValidationResult { get; init; }
        public Func<PlanningRequest, Task<PlanningPreparation>>? OnPrepare { get; init; }
        public Func<PlanningSnapshot, Task>? OnCheckpoint { get; init; }
        public Task CheckpointAsync(PlanningSnapshot snapshot, CancellationToken ct) => OnCheckpoint?.Invoke(snapshot) ?? Task.CompletedTask;
        public Task<PlanningPreparation> PrepareAsync(PlanningRequest request, CancellationToken ct) { PreparationCalls++; return OnPrepare?.Invoke(request) ?? Task.FromResult(Preparation()); }
        public Task<LLMResponse> CallAsync(LLMRequest request, string phase, CancellationToken ct)
        {
            lock (Phases) { Phases.Add(phase); Requests.Add(request); }
            if (OnCall is not null) return OnCall(phase, request, ct);
            JsonNode? json = InvalidJson ? new JsonObject() : phase switch
            {
                "intent" => new JsonObject { ["outcome"] = "ready", ["reason"] = "Clear", ["evidence"] = new JsonArray(), ["questions"] = new JsonArray() },
                "behavior" => JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan),
                "fragment" => request.StructuredOutputSchema?["properties"]?["nodes"] is null ? PlanningModelValues.Workflow(Graph().Workflows[0]) : PlanningFragments.Values(Graph().Workflows[0]),
                "fragment_inputs" or "fragment_contracts" or "fragment_implementation" or "fragment_outputs" => ConstructionResponse(request, phase),
                "semantic_review" => new JsonObject { ["findings"] = new JsonArray() },
                "scenario_inputs" => new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["kind"] = "string", ["text"] = "fixture" }))),
                _ => throw new InvalidOperationException("Unexpected model phase: " + phase)
            };
            return Task.FromResult(new LLMResponse { Json = json, Text = json!.ToJsonString() });
        }
        internal static JsonObject ConstructionResponse(LLMRequest request, string phase)
        {
            var workflow = Graph().Workflows[0];
            workflow.Steps[0].OutputSchema = new() { Type = "object", Properties = [new() { Name = "message", Schema = new() { Type = "string" } }] };
            return PlanningConstruction.Values(workflow, new() { Kind = phase[9..], NodeKeys = (request.StructuredOutputSchema?["properties"]?["nodes"]?["properties"] as JsonObject ?? []).Select(p => p.Key).ToList() });
        }
        public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(string yaml, PlanningRequest request, PlanningPreparation preparation, CancellationToken ct)
        {
            ValidationCalls++; new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            return Task.FromResult(ValidationResult?.Invoke(ValidationCalls) ?? (IReadOnlyList<PlanningDiagnostic>)[]);
        }
        public Task<IReadOnlyList<PlanningScenarioResult>> ValidateScenariosAsync(string yaml, PlanningPreparation preparation, CancellationToken ct)
        {
            ScenarioCalls++;
            return Task.FromResult<IReadOnlyList<PlanningScenarioResult>>([new("nominal", ScenarioOutcome, "Fake integration scenario", [])]);
        }
    }
}
