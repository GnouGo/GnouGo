using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BehaviorRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningPreparation Catalog(string server = "opaque-host", string method = "opaque-tool")
    {
        var preparation = Preparation();
        preparation.Capabilities.Add(new()
        {
            Id = "producer", StepType = "mcp.call", Server = server, Method = method, Kind = "tool",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"content":{"type":"string"}},"required":["content"],"additionalProperties":false}""")!.AsObject()
        });
        return preparation;
    }
    private static PlanningGraph Candidate(string pointer = "/content")
    {
        var graph = Graph();
        graph.Workflows[0].Steps[0] = new() { Key = "greeting", Type = "mcp.call", CapabilityId = "producer" };
        graph.Workflows[0].Outputs[0].Schema = new() { CapabilityId = "producer", SchemaPointer = pointer };
        graph.Workflows[0].Outputs[0].Value.Path = ["content"];
        return graph;
    }
    private static PlanningSnapshot Behavior(PlanningGraph? retained = null, string status = PlanningStatus.Created)
    {
        var state = Session(status); state.IntentChecked = true; state.Preparation = Catalog();
        state.CurrentPhase = PlanningPhase.Behavior; state.Graph = retained;
        return state;
    }
    private static Task<PlanningSnapshot> Send(PlanningSnapshot state, FakeRuntime runtime, string kind = "advance", string? text = null)
        => new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = kind, ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash, Text = text }, runtime, Ct);
    private static JsonNode Json(PlanningGraph graph) => JsonSerializer.SerializeToNode(graph, PlanningJsonContext.Default.PlanningGraph)!;
    private static FakeRuntime Responses(params JsonNode[] responses)
    {
        var count = 0;
        return new() { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = responses[Math.Min(count++, responses.Length - 1)].DeepClone() }) };
    }

    [Theory]
    [InlineData("Return the declared content", "host-a", "read_a")]
    [InlineData("Retourne le contenu déclaré", "renamed-42", "operation_97")]
    [InlineData("Devuelve el contenido declarado", "catalog-z", "op_z")]
    public async Task WrongSchemaPath_GetsOneTargetedRepair_ThenBehaviorReview(string prompt, string server, string method)
    {
        var state = Behavior(); state.Request.Prompt = prompt; state.Preparation = Catalog(server, method);
        var runtime = Responses(Json(Candidate()), Json(Candidate("/output/properties/content")));
        state = await Send(state, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        Assert.Equal(new[] { "behavior", "behavior_repair" }, runtime.Phases);
        Assert.Equal(2, state.BehaviorAssessmentCalls);
        Assert.Contains("/workflows/0/outputs/0/schema", runtime.Requests[1].Prompt);
        Assert.Contains("/output/properties/content", runtime.Requests[0].Prompt);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.ReviewedGraph); Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml);
        Assert.Contains(state.Events, e => e.Kind == "behavior_repair_succeeded");
    }

    [Fact]
    public async Task MalformedShapeAndSemanticDefect_ShareTheTwoCallLimit()
    {
        var runtime = Responses(new JsonObject(), Json(Candidate()));
        var state = await Send(Behavior(), runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.Equal(2, state.BehaviorAssessmentCalls);
        Assert.Null(state.Outcome); Assert.NotNull(state.WaitingSinceUtc);
        Assert.Contains(state.Diagnostics, d => d.Code == "SCHEMA_REFERENCE_INVALID");
        var unchanged = await Send(state, runtime);
        Assert.Equal(state.Revision, unchanged.Revision); Assert.Equal(2, runtime.Requests.Count);
    }

    [Fact]
    public async Task FailedLegacyCandidate_RetryMustReviewBehaviorBeforeElaboration()
    {
        var state = Behavior(Candidate(), PlanningStatus.Failed);
        state.CurrentPhase = null; // compatible older snapshot
        state.Diagnostics.Add(new("PLANNING_FAILED", "$", "The authoritative schema reference is unresolved."));
        var runtime = Responses(Json(Candidate("/output/properties/content")));
        state = await Send(state, runtime, "retry");
        Assert.Equal(PlanningStatus.Created, state.Status);
        Assert.Equal(PlanningPhase.Behavior, state.CurrentPhase);
        Assert.Empty(state.Diagnostics); Assert.NotEmpty(Assert.Single(state.IntentHistory).Diagnostics);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        state = await Send(state, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        Assert.Equal(new[] { "behavior_repair" }, runtime.Phases);
        Assert.Equal(0, runtime.ValidationCalls); Assert.Equal(0, runtime.ScenarioCalls);
        await Assert.ThrowsAsync<PlanningConflictException>(() => Send(state, runtime, "approve"));
    }

    [Fact]
    public async Task RecoveryWithGraph_CanEditWithoutCompiling_AndPreservesSpentBudgets()
    {
        var state = Behavior(Candidate(), PlanningStatus.Recovery);
        state.ClarificationForms = 2; state.ClarificationQuestions = 7; state.BehaviorAssessmentCalls = 2;
        state.Answers.Add(new("Earlier question", new JsonObject { ["answer"] = "Earlier answer" }));
        state.Usage = new LLMUsageBudgetScope(new() { MaxCalls = 10 }).Snapshot;
        state.Diagnostics.Add(new("SCHEMA_REFERENCE_INVALID", "/workflows/0/outputs/0/schema", "Unresolved pointer"));
        state.WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var priorUsage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var edited = await Send(state, new FakeRuntime(), "edit_intent", "Return a greeting");
        Assert.Null(edited.Graph); Assert.Null(edited.Preparation); Assert.False(edited.IntentChecked);
        Assert.Equal(state.Request.SessionId, edited.Request.SessionId);
        Assert.Equal(2, edited.ClarificationForms); Assert.Equal(7, edited.ClarificationQuestions);
        Assert.Equal(priorUsage, JsonSerializer.Serialize(edited.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Single(Assert.Single(edited.IntentHistory).Answers); Assert.Empty(edited.Answers);
        Assert.True(edited.HumanWaitMilliseconds >= 7_200_000); Assert.InRange(edited.ActiveMilliseconds, 0, 10_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepairCannotDropBoundaryOrChangeUnrelatedValidWork(bool dropOutput)
    {
        var before = Candidate(); before.Workflows[0].Steps.Add(new() { Key = "independent", Input = Obj(("nonce", Str("Keep this"))) });
        var after = Candidate("/output/properties/content");
        after.Workflows[0].Steps.Add(new() { Key = "independent", Input = Obj(("nonce", Str(dropOutput ? "Keep this" : "Changed"))) });
        if (dropOutput) after.Workflows[0].Outputs.Clear();
        var state = await Send(Behavior(), Responses(Json(before), Json(after)));
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Single(state.Graph!.Workflows[0].Outputs);
        Assert.Equal("Keep this", state.Graph.Workflows[0].Steps[1].Input.Members[0].Value.Text);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("/content")]
    [InlineData("/output/properties")]
    [InlineData("/output/properties/content~2")]
    [InlineData("/output/properties/missing")]
    public void InvalidPointersAreRejectedWithExactLocations(string pointer)
    {
        var errors = PlanningGraphValidation.Validate(Candidate(pointer), Catalog());
        Assert.Contains(errors, d => d.Code == "SCHEMA_REFERENCE_INVALID" && d.Location == "/workflows/0/outputs/0/schema");
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(Candidate(pointer), Catalog()));
    }

    [Fact]
    public async Task RepairCannotRelaxAProducerGuardWhileFixingItsSchemaReference()
    {
        var before = Candidate();
        before.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        var after = Candidate("/output/properties/content");
        var state = await Send(Behavior(), Responses(Json(before), Json(after)));
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.False(state.Graph!.Workflows[0].Steps[0].If!.Boolean);
        Assert.Contains(state.Diagnostics, d => d.Code == "BEHAVIOR_REPAIR_REGRESSION");
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("structured")]
    public void InvalidOrUndeclaredResultChannelsCannotBeExported(string channel)
    {
        var graph = Candidate("/output/properties/content");
        graph.Workflows[0].Outputs[0].Value.ResultChannel = channel;
        Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, Catalog()));
    }

    [Fact]
    public void MatchingObjectTypesCannotHideAnUndeclaredRequiredBoundaryField()
    {
        var graph = Candidate("/output");
        graph.Workflows[0].Outputs[0].Value.Path.Clear();
        graph.Workflows[0].Outputs[0].Schema = new() { Type = "object", Properties = [new() { Name = "invented", Required = true, Schema = new() { Type = "string" } }] };
        Assert.Contains(PlanningGraphValidation.Validate(graph, Catalog()), d => d.Code == "OUTPUT_TYPE_MISMATCH");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiteralArraysAndEnumsHaveProvableContractsWithoutModelShaping(bool empty)
    {
        var graph = Graph();
        graph.Workflows[0].Steps[0].Input = Obj(("message", new() { Kind = "array", Items = empty ? [] : [Str("ready"), Str("done")] }));
        graph.Workflows[0].Outputs[0].Schema = new() { Type = "array", Items = new() { Type = "string", Enum = ["ready", "done"] } };
        var yaml = new PlanningGraphCompiler().Compile(graph, Preparation());
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(empty ? 0 : 2, result.Outputs?["message"]?.AsArray().Count);
    }

    [Fact]
    public void EscapedPropertyAndArraySchemaPointer_PreserveExactConstraints()
    {
        var catalog = Catalog();
        catalog.Capabilities[0].OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"a/b~c":{"anyOf":[{"type":"string","minLength":2},{"type":"null"}]}}}""")!.AsObject();
        var reference = new PlanningSchema { CapabilityId = "producer", SchemaPointer = "/output/properties/a~1b~0c/anyOf/0" };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"type":"string","minLength":2}"""), PlanningGraphCompiler.ToJsonSchema(reference, catalog)));
        reference.SchemaPointer = "/output/properties/a~1b~0c/anyOf/00";
        Assert.Throws<InvalidOperationException>(() => PlanningGraphCompiler.ToJsonSchema(reference, catalog));
    }

    [Fact]
    public void MixedSchemaAndInventedProducerFields_AreReportedTogether()
    {
        var graph = Candidate();
        graph.Workflows[0].Outputs[0].Value.Path = ["decision"];
        graph.Workflows[0].Steps[0].OutputSchema = new() { CapabilityId = "producer", Properties = [new() { Name = "decision", Schema = new() { Type = "string" } }] };
        graph.Workflows[0].Steps[0].Input = Obj(("structured_output", Obj(("schema_inline", Obj(("type", Str("object")), ("required", new() { Kind = "array", Items = [Str("decision")] }))), ("strict", new() { Kind = "boolean", Boolean = true }))));
        var errors = PlanningGraphValidation.Validate(graph, Catalog());
        Assert.Contains(errors, d => d.Code == "STRUCTURED_OUTPUT_INVALID");
        Assert.Contains(errors, d => d.Code == "SCHEMA_REFERENCE_INVALID" && d.Location.Contains("outputSchema", StringComparison.Ordinal));
        Assert.Contains(errors, d => d.Code == "OUTPUT_REFERENCE_INVALID");
    }

    [Theory]
    [InlineData("opaque-host", "opaque-tool")]
    [InlineData("renamed-producer", "unrelated_operation")]
    public async Task RawAndStructuredResults_CompileAndExecuteAgainstDifferentContracts(string server, string method)
    {
        var catalog = Catalog(server, method);
        var graph = Candidate("/output/properties/content");
        var synthesized = Obj(("schema_inline", Obj(("type", Str("object")),
            ("properties", Obj(("content", Obj(("type", Str("integer")))))),
            ("required", new() { Kind = "array", Items = [Str("content")] }),
            ("additionalProperties", new() { Kind = "boolean", Boolean = false }))),
            ("strict", new() { Kind = "boolean", Boolean = true }));
        graph.Workflows[0].Steps[0].Input = Obj(("structured_output", synthesized), ("model", Str("fake")));
        graph.Workflows[0].Outputs.Add(new() { Name = "structured", Schema = new() { Type = "integer" }, Value = new() { Kind = "output", Source = "greeting", ResultChannel = "structured", Path = ["content"] } });
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer(server, new() { Tools = [new() { Name = method, InputSchema = catalog.Capabilities[0].InputSchema, OutputSchema = catalog.Capabilities[0].OutputSchema }],
            ToolHandlers = new() { [method] = _ => new McpCallResult { Content = new JsonObject { ["content"] = "raw" } } } });
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { McpClientFactory = factory, LLMClient = new StructuredClient() }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("raw", result.Outputs?["message"]?.GetValue<string>());
        Assert.Equal(42, result.Outputs?["structured"]?.GetValue<int>());
        graph.Workflows[0].Outputs[1].Value.ResultChannel = "default";
        Assert.Contains(PlanningGraphValidation.Validate(graph, catalog), d => d.Code == "OUTPUT_TYPE_MISMATCH");
    }

    private sealed class StructuredClient : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
            => Task.FromResult(new LLMResponse { Json = new JsonObject { ["content"] = 42 } });
    }
}
