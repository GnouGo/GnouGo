using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RepairPatchRuntimeTests
{
    [Theory]
    [InlineData("known", true)]
    [InlineData(null, false)]
    [InlineData("wrong-type", false)]
    public async Task ExplicitConstraintRepairDoesNotInventMissingValuesOrDispatchInvalidInputs(string? value, bool succeeds)
    {
        var state = RepairPatchTests.State(); var capability = state.Catalog!.Capabilities[0];
        var effects = new List<JsonNode>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "arbitrary", InputSchema = capability.InputSchema, OutputSchema = capability.OutputSchema }],
            ToolHandlers = new() { ["arbitrary"] = input => { effects.Add(input!.DeepClone()); return new() { Content = new JsonObject() }; } } });
        var model = new Model(value);
        var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "mock" }, McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human() };
        state.Catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Catalog.Capabilities.Add(capability);
        var plan = RepairPatchTests.Apply(state, RepairPatchTests.Edit(state, "/tasks/consumer/inputs/position", "remove"),
            RepairPatchTests.Edit(state, "/tasks/producer/resultType/fields/text/type/nullable", "replace", JsonValue.Create(false)));
        var compiled = new TaskPlanCompiler().Compile(plan, state.Catalog); Assert.Empty(compiled.Diagnostics);
        PlanningConfirmationGuards.Apply(compiled.Graph!, state.Catalog);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, state.Catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), PlannerFixture.Ct);
        Assert.Equal(succeeds, result.Success); Assert.Equal(1, model.Calls);
        if (!succeeds) { Assert.Empty(effects); Assert.NotNull(result.Error); }
        else
        {
            var input = Assert.Single(effects);
            Assert.Equal("known", input["text"]!.GetValue<string>()); Assert.Equal("host", input["fixed"]!.GetValue<string>());
            Assert.Null(input["position"]);
        }
        Assert.True(state.Plan!.Root.Tasks[0].ResultType!.Fields[0].Type.Nullable);
    }

    [Fact]
    public async Task IssuedPatchRecoversExactRequestBeforeRenewedBusinessReview()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        runtime.Respond = (_, _) => new() { Json = BusinessReviewProtocolTests.Report("ok") };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        var accepted = PlanningConstruction.Hash(state.Plan);
        state = await new HybridWorkflowPlanner().AdvanceAsync(BusinessReviewProtocolTests.Clone(state), new()
        {
            Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            PreserveRequirements = true, EditablePaths = ["/tasks/compute/outputs/result"],
            Text = "Revise only the arithmetic operand to three."
        }, runtime, PlannerFixture.Ct);
        var beforeIssueCalls = runtime.Calls.Count;
        runtime.Checkpointed = checkpoint =>
        {
            if (checkpoint.PendingCall?.Purpose != "replan") return;
            runtime.Checkpointed = null;
            throw new OperationCanceledException(); // Persisted before model dispatch, so no uncertain call exists.
        };
        _ = await BusinessReviewProtocolTests.Advance(state, runtime);
        var saved = BusinessReviewProtocolTests.Clone(runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Purpose == "replan" && checkpoint.Status == PlanningStatus.Generating));
        Assert.Equal(beforeIssueCalls, runtime.Calls.Count);
        var issued = saved.PendingCall!.Request;
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var calls = saved.ModelCalls; var corrections = saved.ReplanAttempts;
        var replacement = JsonSerializer.SerializeToNode(saved.Construction!.Candidate!.Plan.Root.Tasks[0].Outputs[0].Value, PlanningJsonContext.Default.TaskValue)!;
        replacement["items"]![1]!["number"] = 3;
        var slot = PlanningRepairPatch.RequestContext(issued)["repair"]!["slots"]![0]!["id"]!.DeepClone();
        var response = new JsonObject { ["patch"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject
        { ["slot"] = slot, ["action"] = "replace", ["value"] = replacement }) } };
        var recovery = new TestRuntime { Respond = (request, purpose) =>
        {
            Assert.Equal("replan", purpose);
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject()) };
        } };
        var repaired = await BusinessReviewProtocolTests.Advance(saved, recovery);
        Assert.Single(recovery.Calls); Assert.Null(repaired.PendingCall);
        Assert.Equal(calls, repaired.ModelCalls); Assert.Equal(corrections + 1, repaired.ReplanAttempts);
        Assert.Equal(accepted, PlanningConstruction.Hash(repaired.Plan));
        Assert.NotEqual(accepted, repaired.Construction!.Candidate!.Hash);
        Assert.False(repaired.Construction.Candidate.BusinessReviewed);
        Assert.Null(repaired.ApprovedHash);
    }

    private sealed class Model(string? value) : ILLMClient
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["text"] = value == "wrong-type" ? JsonValue.Create(42) : JsonValue.Create(value), ["position"] = null } });
        }
    }
}
