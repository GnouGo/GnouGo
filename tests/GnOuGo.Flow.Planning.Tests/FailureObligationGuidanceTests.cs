using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FailureObligationGuidanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnedCollectionFailureReachesTheReportOnlyWhenTheLoopIsRouted(bool route)
    {
        static TaskValue Input(string name) => new() { Kind = "input", Source = name };
        static TaskValue Output(string id, string port) => new() { Kind = "output", Source = id, Port = port };
        var available = new TaskValue { Kind = "predicate", Predicate = "not_equal", Items = [Input("records"), new() { Kind = "null" }] };
        var loop = new PlanTask { Id = "visit", Kind = "foreach", Objective = "Visit the observed records in order.",
            Items = Input("records"), Requires = available, Body = new() { Outputs = [new("record", new() { Kind = "item" })] } };
        var report = new List<TaskOutput> { new("observedStatus", Input("status")),
            new("count", new() { Kind = "array_length", Items = [Output("visit", "record")] }) };
        var plan = new TaskPlan { Inputs = [new() { Name = "records", Type = new() { Kind = "array", Nullable = true, Items = new() { Kind = "number" } } },
            new() { Name = "status", Type = new() { Kind = "string" } }] };
        plan.Root = route ? new() { Tasks = [new() { Id = "eligible", Kind = "conditional", Objective = "Preserve a returned failure.",
            Condition = available, Body = new() { Tasks = [loop], Outputs = report },
            Otherwise = new() { Outputs = [new("observedStatus", Input("status")), new("count", new() { Kind = "number", Number = 0 })] } }],
            Outputs = [new("observedStatus", Output("eligible", "observedStatus")), new("count", Output("eligible", "count"))] }
            : new() { Tasks = [loop], Outputs = report };
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog)));
        var workflow = document.Workflows[document.Entrypoint!];
        var engine = new WorkflowEngine();
        var missing = await engine.ExecuteAsync(workflow, new JsonObject { ["records"] = null, ["status"] = "source unavailable" }, PlannerFixture.Ct);
        Assert.Equal(route, missing.Success);
        if (route) Assert.Equal("source unavailable", missing.Outputs!["observedStatus"]!.ToString());
        var present = await engine.ExecuteAsync(workflow, new JsonObject { ["records"] = new JsonArray(2, 2), ["status"] = "complete" }, PlannerFixture.Ct);
        Assert.True(present.Success, present.Error?.Message);
        Assert.Equal(2, present.Outputs!["count"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("select")]
    [InlineData("implementation")]
    [InlineData("discovery")]
    [InlineData("draft")]
    [InlineData("local_review")]
    [InlineData("global_review")]
    public async Task DecisionsDistinguishFailureTruthfulnessFromRecoveryWithoutChangingAuthority(string phase)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: true);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var unissued = phase switch
        {
            "select" => PlanningCompactConstruction.SelectionRequest(state),
            "implementation" => PlanningCompactConstruction.ImplementationRequest(state),
            "discovery" => PlanningCompactConstruction.Request(PlanningCompactConstruction.DiscoveryGuidance(state),
                PlanningCompactConstruction.DiscoveryContext(state), PlanningCompactConstruction.DiscoverySchema(state), PlanningCompactConstruction.DiscoveryPurpose),
            "draft" => PlanningConstruction.DraftRequest(state, repair: false),
            _ => HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: phase == "global_review").Request
        };
        var purpose = phase switch
        {
            "select" => PlanningCompactConstruction.SelectPurpose,
            "implementation" => PlanningCompactConstruction.ImplementationPurpose,
            "discovery" => PlanningCompactConstruction.DiscoveryPurpose,
            "draft" => PlanningConstruction.DraftPurpose,
            "local_review" => PlanningConstruction.LocalPurpose,
            _ => PlanningConstruction.GlobalPurpose
        };
        var request = PlanningModelCalls.PrepareRequest(state, purpose, unissued.Prompt, unissued.StructuredOutputSchema!.AsObject());
        Assert.Contains("Do not infer mandatory recovery or partial delivery from truthfulness or cleanup alone", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Preserve explicitly requested recovery", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("raised operation failure", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("False requires stops the workflow", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("place the effect and its dependent actions inside conditional.body", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("otherwise and continue to the requested result", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("already returned data", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("approval and freshness checks run inside the operation", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        if (phase.EndsWith("review", StringComparison.Ordinal))
        {
            var schema = request.StructuredOutputSchema!.ToJsonString();
            Assert.Contains("needs_change", schema, StringComparison.Ordinal);
            Assert.Contains("needs_context", schema, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Keep errors explicit and always release the resource.")]
    [InlineData("After a failed item, continue with every remaining item and save a partial report; always release the resource.")]
    public async Task BothFailureContractsRemainVerbatimAtSelectionAndReview(string original)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready(advisoryOptimization: true);
        var compact = state.Construction!.Compact!;
        compact.Sources.Add(new() { Id = "failure_contract", Kind = "global", Indexed = true, Text = original,
            Version = PlanningGraphCompiler.Fingerprint(original) });
        compact.GlobalSourceIds.Add("failure_contract");
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        foreach (var request in new[] { PlanningCompactConstruction.SelectionRequest(state),
                     PlanningCompactConstruction.ImplementationRequest(state),
                     HybridWorkflowPlanner.CreateConstructionReviewRequest(state, global: true).Request })
        {
            Assert.Contains(original, request.Prompt, StringComparison.Ordinal);
            var issued = PlanningModelCalls.PrepareRequest(state, request.StructuredOutputSchema!["title"]!.ToString(),
                request.Prompt, request.StructuredOutputSchema.AsObject());
            Assert.Contains("Preserve explicitly requested recovery", issued.Prompt, StringComparison.Ordinal);
            Assert.Contains(original, issued.Prompt, StringComparison.Ordinal);
        }
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    [Fact]
    public async Task StrictReviewKeepsOriginalContextAndVerdictChoicesWhenGuidanceWouldExceedItsCapturedTarget()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        const string prompt = "Assess actual behavior.\n{\"original\":\"Continue after each failed item and save partial results.\"}";
        var schema = JsonNode.Parse("""{"type":"object","properties":{"verdict":{"type":"string","enum":["ok","needs_change","needs_context"]}},"required":["verdict"],"additionalProperties":false}""")!.AsObject();
        var original = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.GlobalPurpose, prompt, schema);
        Assert.Contains("Do not infer mandatory recovery", original.Prompt, StringComparison.Ordinal);
        var bare = JsonSerializer.Deserialize(JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        bare.Prompt = prompt;
        var limit = checked((int)ModelRequestAdmission.EstimateTokens(bare) + 1);
        var policy = state.Request.Options["construction_limits"]!["phasePolicy"]!["review"]!;
        policy["targetInputTokens"] = limit;
        policy["maxInputTokens"] = limit;
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.GlobalPurpose, prompt, schema);
        Assert.Equal(prompt, issued.Prompt);
        Assert.True(JsonNode.DeepEquals(original.StructuredOutputSchema, issued.StructuredOutputSchema));
        PlannerFixture.ObserveInputBudget(ModelRequestAdmission.EstimateTokens(issued), limit);
        Assert.NotEqual(original.ClientRequestId, issued.ClientRequestId);
        Assert.Equal(issued.ClientRequestId, PlanningModelCalls.PrepareRequest(state, PlanningConstruction.GlobalPurpose, prompt, schema).ClientRequestId);
        Assert.Null(state.PendingCall);
        Assert.Equal(0, state.ModelCalls);
    }
}
