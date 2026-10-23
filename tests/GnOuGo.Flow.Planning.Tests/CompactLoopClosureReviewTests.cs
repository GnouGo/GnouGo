using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactLoopClosureReviewTests
{
    [Fact]
    public async Task ClosureReviewExplainsAggregationWhileStillCorrectingAWrongPerItemExport()
    {
        var (state, runtime) = await Ready("foreach");
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("wrong", Assert.Single(state.Construction!.Candidate!.Extension.Outputs).Value.Port);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        var reviews = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == "replan") return Wire(request, Draft(request, "value"));
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            AssertClosureRequest(request, aggregation: true);
            var context = Context(request);
            Assert.Equal(reviews == 0 ? "wrong" : "value", context["draft"]!["outputs"]![0]!["value"]!["port"]!.ToString());
            return Wire(request, reviews++ == 0 ? new()
            {
                ["findings"] = new JsonArray(new JsonObject { ["kind"] = "inconsistency", ["location"] = "/draft/outputs/0",
                    ["evidence"] = new JsonArray("/draft/outputs/0", "/requirements/0"),
                    ["explanation"] = "The selected export uses the unchanged item instead of the doubled output.",
                    ["uncertainty"] = "Execution remains unverified." }),
                ["uncertainty"] = "The foreach performs collection; this finding concerns the chosen per-item value.", ["evidenceRequests"] = null
            } : Reviewed());
        };
        state = await Advance(state, runtime);
        Assert.Equal("CONSTRUCTION_BUSINESS_REVIEW", Assert.Single(state.Diagnostics).Code);
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(2, reviews);
        Assert.Single(state.Construction!.Checkpoints);
        var loop = Assert.Single(state.Plan!.Root.Tasks);
        Assert.Equal("value", Assert.Single(loop.Body!.Outputs).Value.Port);
        Assert.Equal(100, loop.MaxItems); Assert.Equal(1, loop.MaxConcurrency);
        var interfaces = TaskPlanCompiler.ValidateConstruction(state.Plan, state.Catalog!, state.Request,
            state.Construction.OpenScopes.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<TaskValue>>
            {
                ["/tasks/iteration/body"] = [new() { Kind = "output", Source = "per_item", Port = "value" }],
                ["/root"] = [new() { Kind = "output", Source = "iteration", Port = "result" }]
            });
        Assert.Empty(interfaces.Diagnostics);
        Assert.Equal("number", Assert.Single(interfaces.Interfaces["/tasks/iteration/body"]).Schema["type"]!.ToString());
        var outer = Assert.Single(interfaces.Interfaces["/root"]).Schema;
        Assert.Equal("array", outer["type"]!.ToString());
        Assert.Equal("number", outer["items"]!["type"]!.ToString());
        Assert.Equal("in_progress", Assert.Single(state.Construction.Requirements).Status);
        Assert.False(state.Construction.Complete);
    }

    [Fact]
    public async Task ASequenceWithTheSameScopePathReceivesNoForeachAggregationClaim()
    {
        var (state, runtime) = await Ready("sequence");
        state = await Advance(state, runtime);
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            AssertClosureRequest(request, aggregation: false);
            return Wire(request, Reviewed());
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Single(state.Construction!.Checkpoints);
    }

    [Fact]
    public async Task PreviouslyIssuedClosureReviewKeepsItsExactContextSchemaAndAccounting()
    {
        var (state, runtime) = await Ready("foreach");
        state = await Advance(state, runtime);
        state.Construction!.Compact!.PresentationVersion = null;
        state.Construction.CandidateFailureIdentityVersion = null;
        state.Request.Options.Remove("construction_presentation_version");
        state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
        state.Construction.Limits.PhasePolicy = null;
        runtime.Respond = (_, _) => throw new OperationCanceledException();
        _ = await Advance(state, runtime);
        var saved = PlannerFixture.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Purpose == PlanningConstruction.LocalPurpose));
        var issued = saved.PendingCall!.Request;
        AssertClosureRequest(issued, aggregation: false);
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var context = saved.Construction!.PendingReviewContext!.ToJsonString();
        var hash = saved.Construction.PendingReviewHash;
        var calls = saved.ModelCalls;
        saved.Status = PlanningStatus.Generating;
        saved.Construction.Compact!.PresentationVersion = 2;
        saved.Request.Options["construction_presentation_version"] = 2;
        var receipt = Wire(issued, Reviewed());
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            Assert.Equal(context, saved.Construction.PendingReviewContext!.ToJsonString());
            Assert.Equal(hash, saved.Construction.PendingReviewHash);
            return receipt;
        } };
        state = await Advance(saved, recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Single(recovery.Calls); Assert.Equal(calls, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.Single(state.Construction!.Checkpoints);
        Assert.Equal("wrong", Assert.Single(state.Plan!.Root.Tasks[0].Body!.Outputs).Value.Port);
    }

    private static void AssertClosureRequest(LLMRequest request, bool aggregation)
    {
        var context = Context(request);
        Assert.Equal("close", context["step"]!["kind"]!.ToString());
        Assert.Equal("/tasks/iteration/body", context["step"]!["scope"]!.ToString());
        Assert.Equal(aggregation, request.Prompt.Contains("Each foreach body output becomes an array", StringComparison.Ordinal));
        Assert.Equal("number", context["scopeInterface"]!["outputs"]![0]!["schema"]!["type"]!.ToString());
        if (context["constructionStage"]!["closeRequirementIds"] is JsonArray claims) Assert.Empty(claims);
        else Assert.False(context["constructionStage"]!["requirementCompletionClaimed"]!.GetValue<bool>());
        Assert.InRange(PlanningRequestMeasurements.Measure(request, PlanningConstruction.LocalPurpose, 4000).EstimatedInputTokens, 1, 4000);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(string kind)
    {
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            var context = Context(request);
            if (purpose == PlanningCompactConstruction.IntentPurpose)
                return Wire(request, new() { ["sourceId"] = context["sources"]![0]!["id"]!.DeepClone(),
                    ["clauses"] = new JsonArray(new JsonObject { ["text"] = context["sources"]![0]!["text"]!.ToString(), ["kind"] = "requirement", ["interfaces"] = "none" }) });
            if (purpose == PlanningCompactConstruction.SelectPurpose)
                return Wire(request, new() { ["baseHash"] = request.StructuredOutputSchema!["properties"]!["baseHash"]!["enum"]![0]!.DeepClone(), ["evidenceRequests"] = null, ["clarifications"] = null,
                    ["step"] = new JsonObject { ["scope"] = "/tasks/iteration/body", ["kind"] = "close", ["objective"] = "Export doubled values through the enclosing container.",
                        ["requirementIds"] = new JsonArray("req_0"), ["inputs"] = new JsonArray(new JsonObject { ["kind"] = "output", ["source"] = "per_item", ["port"] = "value" },
                            new JsonObject { ["kind"] = "output", ["source"] = "per_item", ["port"] = "wrong" }), ["dependencyTaskIds"] = new JsonArray(),
                        ["expectedOutputs"] = new JsonArray("Ordered doubled values from the enclosing container."), ["closeRequirementIds"] = new JsonArray(), ["outputNames"] = new JsonArray(), ["groupId"] = null } });
            Assert.Equal(PlanningConstruction.DraftPurpose, purpose);
            return Wire(request, Draft(request, "wrong"));
        } };
        var state = await Advance(new() { Request = new() { TenantId = "loop-closure", Prompt = "Double the original numbers [2,2], preserving their order and repetition.",
            Options = new() { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        state.Plan = new() { Root = new() { Tasks = [new()
        {
            Id = "iteration", Kind = kind, Objective = "Retained local work.", MaxItems = 100, MaxConcurrency = 1,
            Items = kind == "foreach" ? new() { Kind = "array", Items = [new() { Kind = "number", Number = 2 }, new() { Kind = "number", Number = 2 }] } : null,
            Body = new() { Tasks = [new() { Id = "per_item", Kind = "value", Objective = "Retained doubled and original values.",
                Outputs = [new("value", new() { Kind = "arithmetic", Text = "multiply", Items = [
                    kind == "foreach" ? new() { Kind = "item" } : new() { Kind = "number", Number = 2 },
                    new() { Kind = "number", Number = 2 }] }),
                    new("wrong", kind == "foreach" ? new() { Kind = "item" } : new() { Kind = "number", Number = 2 })] }] }
        }] } };
        state.Construction!.OpenScopes = ["/root", "/tasks/iteration/body"];
        state.Construction.Requirements[0].Status = "in_progress";
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        return (state, runtime);
    }

    private static JsonObject Draft(LLMRequest request, string port) => new()
    {
        ["baseHash"] = Context(request)["baseHash"]!.DeepClone(), ["requirements"] = null, ["commitments"] = null,
        ["extension"] = JsonSerializer.SerializeToNode(new PlanningConstructionExtension
        {
            Scope = "/tasks/iteration/body", Outputs = [new("result", new() { Kind = "output", Source = "per_item", Port = port })],
            CloseScopes = ["/tasks/iteration/body"], RequirementIds = ["req_0"]
        }, PlanningJsonContext.Default.PlanningConstructionExtension),
        ["discoveryRequests"] = null, ["clarifications"] = null, ["evidenceRequests"] = null, ["rollbackTask"] = null, ["rollbackReason"] = null, ["reselectStep"] = null
    };

    private static JsonObject Reviewed() => new() { ["findings"] = new JsonArray(), ["uncertainty"] = "Execution and remaining obligations are unverified.", ["evidenceRequests"] = null };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static LLMResponse Wire(LLMRequest request, JsonObject response) => new()
    { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
