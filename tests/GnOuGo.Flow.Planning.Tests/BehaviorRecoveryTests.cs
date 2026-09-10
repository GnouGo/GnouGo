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
    [Fact]
    public void RevisionContextCannotPromoteAnUnreviewedCandidateToBaselineEvidence()
    {
        var state = Behavior(); state.Request.Baseline = Graph();
        state.BehaviorRevision = new() { Text = "Change the result" };
        state.BehaviorAssessment.Candidate = new() { ["summary"] = "Invented external authorization" };
        Assert.DoesNotContain("Invented external authorization", PlanningContext.BaselineText(state));
        state.BehaviorPlan = BehaviorPlan(); state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        Assert.Equal(JsonSerializer.Serialize(state.BehaviorPlan, PlanningJsonContext.Default.PlanningBehaviorPlan), PlanningContext.BaselineText(state));
    }

    [Fact]
    public void HumanRevisionCanInsertOnePortWithoutReplacingTheCollection()
    {
        var candidate = JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan)!.AsObject();
        var schema = PlanningSchemas.Behavior(Preparation());
        var target = Assert.Single(PlanningBehaviorPatches.Scope(candidate, schema,
            [new("BEHAVIOR_REVISION_REQUIRED", "/workflows/0/inputs/0", "Add the requested runtime input", Rule: "revision_add")]));
        Assert.True(target.Add);
        var response = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target.Id,
            ["value"] = new JsonObject { ["name"] = "source", ["description"] = "Required runtime source", ["required"] = true } }) };
        var result = PlanningExactPatches.Apply(candidate, response, [target], PlanningExactPatches.Schema([target], schema));
        Assert.Equal("source", result["workflows"]![0]!["inputs"]![0]!["name"]!.ToString());
        Assert.True(JsonNode.DeepEquals(candidate["workflows"]![0]!["steps"], result["workflows"]![0]!["steps"]));
        Assert.Empty(candidate["workflows"]![0]!["inputs"]!.AsArray());
        Assert.Empty(PlanningBehaviorPatches.Scope(candidate, schema,
            [new("BEHAVIOR_REVISION_REQUIRED", "/workflows/0/inputs", "Cannot replace a collection", Rule: "revision_add")]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningPreparation Catalog(string server = "opaque-host", string method = "opaque-tool")
    {
        var preparation = Preparation();
        preparation.Capabilities.Add(new()
        {
            Id = "producer",
            StepType = "mcp.call",
            Server = server,
            Method = method,
            Kind = "tool",
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
        var state = Session(status); state.Intent.Checked = true; state.Preparation = Catalog();
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

    [Fact]
    public async Task HumanRevisionLocatesThenPatchesRetainedBehaviorBeforeRenewedReview()
    {
        var state = Behavior(); state.BehaviorPlan = BehaviorPlan(); state.Status = PlanningStatus.BehaviorReview;
        state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        var revised = await Send(state, new(), "revise", "Describe the greeting as formal.");
        Assert.NotNull(revised.BehaviorAssessment.Candidate); Assert.Null(revised.ApprovedBehaviorHash);
        revised.Preparation = state.Preparation; revised.Intent.Checked = true;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase == "behavior_revision_scope")
            {
                var context = JsonNode.Parse(request.Prompt![ (request.Prompt.IndexOf("Coordinates:\n", StringComparison.Ordinal) + "Coordinates:\n".Length)..])!.AsObject();
                var target = context.Single(p => p.Value!["path"]!.ToString() == "/workflows/0/steps/0/purpose" && p.Value["operation"]!.ToString() == "replace").Key;
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["target"] = target, ["evidence"] = "formal" }) } });
            }
            Assert.Equal("behavior_repair", phase);
            var variant = Assert.Single(request.StructuredOutputSchema!["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray());
            var id = variant!["properties"]!["target"]!["enum"]![0]!.ToString();
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = id, ["value"] = "Return a formal greeting" }) } });
        } };
        revised = await Send(revised, runtime);
        revised = await Send(revised, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, revised.Status);
        Assert.Equal("greeting", revised.BehaviorPlan!.Workflows[0].Steps[0].Key);
        Assert.Equal("Return a formal greeting", revised.BehaviorPlan.Workflows[0].Steps[0].Purpose);
        Assert.Equal(["behavior_revision_scope", "behavior_repair"], runtime.Phases);
        Assert.Single(revised.RepairAllowances);
    }

    [Fact]
    public void WorkflowCallOwnershipProjectsFromAnExplicitUniqueOperationClaim()
    {
        var preparation = Preparation();
        preparation.Capabilities.Add(new() { Id = "call", Resolution = "local", Required = true, OperationIds = ["call_operation"] });
        var plan = BehaviorPlan();
        plan.Workflows[0].Steps = [new() { Key = "invoke", Kind = "workflow", WorkflowKey = "child", Purpose = "Invoke the child", OperationIds = ["call_operation"], InputDependencies = [] }];
        plan.Workflows.Add(new() { Key = "child", Purpose = "Reusable child" });
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.Equal(["call_operation"], plan.Workflows[0].OperationIds);
        Assert.DoesNotContain(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Rule is "behavior_08" or "behavior_22");
        plan.Workflows[0].OperationIds.Clear();
        plan.Workflows[1].Steps.Add(new() { Key = "ambiguous", OperationIds = ["call_operation"] });
        PlanningBehaviorPlans.CompleteOwnership(plan, preparation);
        Assert.Empty(plan.Workflows[0].OperationIds);
        Assert.Empty(plan.Workflows[1].OperationIds);
    }

    [Fact]
    public async Task BehaviorRepairsUseFiveDurableAttemptsAndRetainUnrelatedIdentities()
    {
        var plan = BehaviorPlan(); plan.Workflows[0].Steps = Enumerable.Range(0, 6).Select(i => new PlanningBehaviorNode
            { Key = "node_" + i, Purpose = "", InputDependencies = [] }).ToList();
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase == "behavior") return Task.FromResult(new LLMResponse { Json = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan) });
            Assert.Equal("behavior_repair", phase);
            var target = request.StructuredOutputSchema!["properties"]!["patches"]!["items"]!["anyOf"]![0]!["properties"]!["target"]!["enum"]![0]!.ToString();
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target, ["value"] = "Return the declared result" }) } });
        } };
        var state = Behavior(); state.Request.MaxRepairsPerWorkflowGate = 5;
        for (var i = 0; i < 10 && !PlanningStatus.IsWaiting(state.Status); i++) state = await Send(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPAIR_EXHAUSTED");
        Assert.Equal(5, Assert.Single(state.RepairAllowances).Attempts);
        Assert.Single(runtime.Phases, p => p == "behavior");
        Assert.Equal(5, runtime.Phases.Count(p => p == "behavior_repair"));
        Assert.Equal(plan.Workflows[0].Steps.Select(n => n.Key), state.BehaviorAssessment.Candidate!["workflows"]![0]!["steps"]!.AsArray().Select(n => n!["key"]!.ToString()));
    }

    [Fact]
    public async Task RetainedInvalidDependencyUsesOneExactPatchAndScopedInputNames()
    {
        var state = Behavior(); state.BehaviorPlan = BehaviorPlan();
        state.BehaviorPlan.Workflows[0].Inputs.Add(new("resource", "Dynamic resource", true));
        state.BehaviorPlan.Workflows[0].Steps[0].InputDependencies = ["producer_step"];
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_repair", phase);
            var variant = request.StructuredOutputSchema!["properties"]!["patches"]!["items"]!["anyOf"]!.AsArray()
                .Single(v => v!["properties"]!["value"]?["enum"] is JsonArray values && values.Any(x => x?.ToString() == "resource"));
            var target = variant!["properties"]!["target"]!["enum"]![0]!.ToString();
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["target"] = target, ["value"] = "resource" }) } });
        } };
        var result = await Send(state, runtime);
        Assert.Equal(PlanningStatus.BehaviorReview, result.Status);
        var request = Assert.Single(runtime.Requests);
        Assert.Contains("producer_step", request.Prompt); Assert.Contains("Allowed inputs: resource", request.Prompt);
        Assert.Null(request.StructuredOutputSchema!["$defs"]?["behaviorNode"]);
        Assert.Equal("resource", Assert.Single(result.BehaviorPlan!.Workflows[0].Steps[0].InputDependencies!));
    }

    [Fact]
    public async Task RecoveryWithGraph_CanEditWithoutCompiling_AndPreservesSpentBudgets()
    {
        var state = Behavior(Candidate(), PlanningStatus.Recovery);
        state.Intent.Forms = 2; state.Intent.Questions = 7; state.BehaviorAssessmentCalls = 2;
        state.Intent.Answers.Add(new("Earlier question", new JsonObject { ["answer"] = "Earlier answer" }));
        state.Usage = new LLMUsageBudgetScope(new() { MaxCalls = 10 }).Snapshot;
        state.Diagnostics.Add(new("SCHEMA_REFERENCE_INVALID", "/workflows/0/outputs/0/schema", "Unresolved pointer"));
        state.WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var priorUsage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var edited = await Send(state, new FakeRuntime(), "edit_intent", "Return a greeting");
        Assert.Null(edited.Graph); Assert.Null(edited.Preparation); Assert.False(edited.Intent.Checked);
        Assert.Equal(state.Request.SessionId, edited.Request.SessionId);
        Assert.Equal(2, edited.Intent.Forms); Assert.Equal(7, edited.Intent.Questions);
        Assert.Equal(priorUsage, JsonSerializer.Serialize(edited.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Single(Assert.Single(edited.Intent.History).Answers); Assert.Empty(edited.Intent.Answers);
        Assert.True(edited.HumanWaitMilliseconds >= 7_200_000); Assert.InRange(edited.ActiveMilliseconds, 0, 10_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PatchCannotDropBoundaryOrChangeUnrelatedValidWork(bool dropOutput)
    {
        var graph = Candidate(); graph.Workflows[0].Steps.Add(new() { Key = "independent", Input = Obj(("nonce", Str("Keep this"))) });
        var patch = new JsonObject
        {
            ["patches"] = new JsonArray(new JsonObject
            {
                ["workflow"] = "main",
                ["node"] = dropOutput ? null : "independent",
                ["field"] = dropOutput ? "outputs" : "input",
                ["value"] = dropOutput ? new JsonArray() : PlanningFixtures.Workflow(new() { Steps = [new() { Input = Obj(("nonce", Str("Changed"))) }] })["steps"]![0]!["input"]!.DeepClone()
            })
        };
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(graph, patch, new HashSet<string>(), Catalog()));
        Assert.Single(graph.Workflows[0].Outputs);
        Assert.Equal("Keep this", graph.Workflows[0].Steps[1].Input.Members[0].Value.Text);
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
    public void RepairCannotRelaxAProducerGuardWhileFixingItsSchemaReference()
    {
        var graph = Candidate(); graph.Workflows[0].Steps[0].If = new() { Kind = "boolean", Boolean = false };
        var diagnostics = PlanningGraphValidation.Validate(graph, Catalog());
        var scope = PlanningPatches.Scope(graph, diagnostics);
        var patch = new JsonObject { ["patches"] = new JsonArray(new JsonObject { ["workflow"] = "main", ["node"] = "greeting", ["field"] = "if", ["value"] = null }) };
        Assert.Throws<InvalidOperationException>(() => PlanningPatches.Apply(graph, patch, scope, Catalog()));
        Assert.False(graph.Workflows[0].Steps[0].If!.Boolean);
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
        factory.RegisterServer(server, new()
        {
            Tools = [new() { Name = method, InputSchema = catalog.Capabilities[0].InputSchema, OutputSchema = catalog.Capabilities[0].OutputSchema }],
            ToolHandlers = new() { [method] = _ => new McpCallResult { Content = new JsonObject { ["content"] = "raw" } } }
        });
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
