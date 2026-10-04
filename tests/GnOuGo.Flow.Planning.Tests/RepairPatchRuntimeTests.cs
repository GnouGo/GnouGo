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
    public async Task ANewPatchRequestCanBeRecoveredWithoutRedispatchOrCounterChanges()
    {
        var state = RepairPatchTests.State(); var runtime = new TestRuntime();
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = PlanningCorpus.Greeting(); state.Plan.Root.Outputs.Add(new("broken", new() { Kind = "output", Source = "absent", Port = "value" }));
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        var request = new LLMRequest { Prompt = HybridWorkflowPlanner.Prompt(state), StructuredOutputSchema = PlanningSchemas.Proposal(state), ClientRequestId = "retained-patch" };
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = "replan", Request = request }; state.ModelCalls = 7; state.ReplanAttempts = 1;
        var payload = new JsonObject { ["clarifications"] = null, ["discoveryRequests"] = null, ["patch"] = new JsonObject { ["edits"] = new JsonArray((JsonNode)RepairPatchTests.Edit(state, "/root/outputs/broken", "replace", JsonNode.Parse("""{"kind":"string","text":"explicit"}"""))) } };
        runtime.Respond = (issued, _) => { Assert.Equal(request.Prompt, issued.Prompt); Assert.True(JsonNode.DeepEquals(request.StructuredOutputSchema, issued.StructuredOutputSchema)); return new() { Json = payload }; };
        var restored = PlannerFixture.Clone(state);
        var result = await new HybridWorkflowPlanner().AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, result.Status); Assert.Equal(7, result.ModelCalls); Assert.Equal(1, result.ReplanAttempts);
        Assert.Equal("retained-patch", Assert.Single(runtime.Calls).ClientRequestId); Assert.Null(result.PendingCall);
        PlanningArtifactApproval.Verify(result);
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
