using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class BindingBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(PlanningSession State, TestRuntime Runtime)> Setup()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.SemanticPlan = new() { Summary = "Bind independent complete actions", Actions = Enumerable.Range(0, 3).Select(i => new SemanticAction
        { Id = "a" + i, Kind = "calculate", Purpose = new string((char)('a' + i), 6000), Outputs = [new("value", "Computed value")], After = i == 0 ? [] : ["a" + (i - 1)] }).ToList(), Outputs = [new("last", "a2.value")] };
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages) state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "none_of_the_above", [], "Pure calculation.")).ToList()));
        state.Grounding.Selections = state.SemanticPlan.Actions.Select(a => new GroundingSelection(a.Id, [], "Pure calculation.")).ToList();
        state.Request.Generation.MaxInputTokensPerRequest = 7500;
        runtime.Respond = request =>
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..]));
            var context = JsonNode.Parse(ref reader)!;
            var actions = context["semanticPlan"]!["actions"]!.AsArray();
            var plan = new GroundedPlan { Summary = "Bound batch", Operations = actions.Select(a => (GroundedOperation)new CalculateGroundedOperation
            { Id = a!["id"]!.ToString(), SemanticAction = a["id"]!.ToString(), BusinessOutputs = [new("value", [])], Value = new() { Kind = "number", Number = 1 } }).ToList(),
                Outputs = context["semanticPlan"]?["outputs"] is null ? [] : [new("last", new() { Kind = "result", Source = "a2" })] };
            return new() { Json = PlanningJsonTransport.ModelGrounded(PlanningJsonTransport.Grounded(plan), request.StructuredOutputSchema!) };
        };
        return (state, runtime);
    }
    [Fact]
    public async Task CompleteBatchesPreserveActionsAndRevalidateAcrossRestarts()
    {
        var (state, runtime) = await Setup();
        Assert.True(GroundedBindingBatches.Required(state));
        for (var i = 0; i < 3 && state.GroundedPlan is null; i++)
        {
            await GroundedBindingBatches.ApplyAsync(state, runtime, Ct);
            Assert.Empty(state.Diagnostics);
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        }
        Assert.NotNull(state.GroundedPlan); Assert.Null(state.BindingProgress);
        Assert.Equal(new[] { "a0", "a1", "a2" }, state.GroundedPlan.Operations.Select(o => o.SemanticAction));
        Assert.InRange(runtime.Calls.Count, 2, 3);
        Assert.All(runtime.Calls, r => Assert.True(PlanningJsonTransport.EstimateInputTokens(r.Prompt, r.StructuredOutputSchema!.AsObject()) <= 7500));
        Assert.Empty(CapabilityGrounder.ValidateBindings(state));
        Assert.NotNull(GroundedPlanValidator.Validate(state.GroundedPlan, state.Catalog!).Plan);
    }
    [Fact]
    public async Task ComplexBindingsReserveReasoningSpaceWithinTheEightCallBudget()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.Catalog.Capabilities.Add(new() { Id = "renamed_operation", StepType = "mcp.call", InputSchema = new JsonObject
        { ["type"] = "object", ["properties"] = new JsonObject(Enumerable.Range(0, 10).Select(i => new KeyValuePair<string, JsonNode?>("arg" + i, new JsonObject { ["type"] = "string" }))) } });
        state.SemanticPlan = new() { Actions = Enumerable.Range(0, 9).Select(i => new SemanticAction { Id = "a" + i, Outputs = [new("evidence", "Original observations")] }).ToList() };
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages) state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "matched", [new("renamed_operation", "Declared behavior")], "Covered")).ToList()));
        state.Grounding.Selections = state.SemanticPlan.Actions.Select(a => new GroundingSelection(a.Id, ["renamed_operation"], "Declared behavior")).ToList();
        state.ModelCalls = 4; // Semantic planning, two complete catalog pages and concrete selection.
        runtime.Respond = request =>
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..]));
            var actions = JsonNode.Parse(ref reader)!["semanticPlan"]!["actions"]!.AsArray();
            Assert.InRange(actions.Count, 1, 3);
            var plan = new GroundedPlan { Operations = actions.Select(a => (GroundedOperation)new InvokeGroundedOperation
            { Id = a!["id"]!.ToString(), SemanticAction = a["id"]!.ToString(), Capability = "renamed_operation", BusinessOutputs = [new("evidence", [])] }).ToList() };
            return new() { Json = PlanningJsonTransport.ModelGrounded(PlanningJsonTransport.Grounded(plan), request.StructuredOutputSchema!) };
        };
        for (var i = 0; i < 3; i++) { await GroundedBindingBatches.ApplyAsync(state, runtime, Ct); Assert.Empty(state.Diagnostics); }
        Assert.NotNull(state.GroundedPlan); Assert.Equal(9, state.GroundedPlan.Operations.Count);
        Assert.Equal(7, state.ModelCalls); Assert.Equal(8, state.Request.MaxModelCalls);
        Assert.Equal(8192, state.Request.Generation.MaxOutputTokens); Assert.Equal(3, runtime.Calls.Count);
    }
    [Fact]
    public async Task InsufficientRemainingCallsStopsBeforeDispatch()
    {
        var (state, runtime) = await Setup(); state.ModelCalls = 7;
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => GroundedBindingBatches.ApplyAsync(state, runtime, Ct));
        Assert.Equal("BINDING_BUDGET_INSUFFICIENT", error.Code); Assert.Empty(runtime.Calls); Assert.Null(state.PendingCall);
        Assert.StartsWith("/actions/", error.Details!["location"]!.ToString());
    }
    [Fact]
    public async Task ReloadedPrefixCannotInventAContract()
    {
        var (state, runtime) = await Setup();
        await GroundedBindingBatches.ApplyAsync(state, runtime, Ct);
        Assert.NotNull(state.BindingProgress);
        ((CalculateGroundedOperation)state.BindingProgress.Accepted.Operations[0]).Value = new() { Kind = "result", Source = "unissued" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GroundedBindingBatches.ApplyAsync(state, runtime, Ct));
        Assert.Single(runtime.Calls);
    }
    [Fact]
    public async Task DecisionInLaterBatchPreservesAcceptedPrefixAndCallBudget()
    {
        var (state, runtime) = await Setup();
        await GroundedBindingBatches.ApplyAsync(state, runtime, Ct);
        var prefix = PlanningJsonTransport.Grounded(state.BindingProgress!.Accepted).ToJsonString();
        var completed = state.BindingProgress.CompletedActions.ToArray();
        var original = runtime.Respond!;
        runtime.RawDecisionResponse = true;
        runtime.Respond = request =>
        {
            var result = original(GnOuGo.Planning.Examples.PlanningCorpus.DecisionResultRequest(request)).Json!;
            var proposal = PlanningDecisionTests.Proposal();
            proposal["decision"]!["options"]![0]!["result"] = result.DeepClone();
            proposal["decision"]!["options"]![1]!["result"] = result.DeepClone();
            proposal["decision"]!["options"]![1]!["result"]!["operations"]![0]!["implementation"]!["value"]!["number"] = 2;
            return new() { Json = proposal };
        };
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.WaitingForDecision, state.Status);
        Assert.Equal(prefix, PlanningJsonTransport.Grounded(state.BindingProgress!.Accepted).ToJsonString());
        Assert.DoesNotContain(state.PendingDecision!.ActionIds, completed.Contains);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        state = await planner.AdvanceAsync(state, new() { Kind = "answer_decision", ExpectedRevision = state.Revision, DecisionAnswer = new(state.PendingDecision!.Id, "casual") }, runtime, Ct);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        var applied = state.GroundedPlan ?? state.BindingProgress!.Accepted;
        Assert.All(applied.Operations.Where(o => completed.Contains(o.SemanticAction)), o => Assert.Equal(1, ((CalculateGroundedOperation)o).Value.Number));
        Assert.Contains(applied.Operations, o => !completed.Contains(o.SemanticAction) && ((CalculateGroundedOperation)o).Value.Number == 2);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(2, runtime.Calls.Count);
    }
    [Fact]
    public async Task LiteralArgumentsRetainTheirRangeAndFiniteValueProofs()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, Ct);
        catalog.Capabilities.Add(new() { Id = "bounded", StepType = "mcp.call", InputSchema = JsonNode.Parse("""
            {"type":"object","required":["duration","confirm"],"additionalProperties":false,"properties":{"duration":{"type":"integer","minimum":1,"maximum":60000},"confirm":{"type":"boolean","const":false}}}
            """)!.AsObject() });
        var op = new InvokeGroundedOperation { Id = "bounded", Capability = "bounded", Arguments = [new("duration", new() { Kind = "number", Number = 30000 }), new("confirm", new() { Kind = "boolean", Boolean = false })] };
        var plan = new GroundedPlan { Operations = [op] };
        Assert.NotNull(GroundedPlanValidator.Validate(plan, catalog).Plan);
        op.Arguments[0].Value.Number = 60001;
        var invalid = GroundedPlanValidator.Validate(plan, catalog);
        Assert.Null(invalid.Plan); Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("duration", StringComparison.Ordinal));
    }
    [Fact]
    public void UntypedOneOfBranchesUseDeclaredDiscriminatorsWithoutLosingExclusivity()
    {
        var actual = JsonNode.Parse("""{"type":"object","required":["mode","text"],"additionalProperties":false,"properties":{"mode":{"type":"string","enum":["alpha"]},"text":{"type":"string"}}}""")!.AsObject();
        var expected = JsonNode.Parse("""{"type":"object","required":["mode"],"properties":{"mode":{"type":"string","enum":["alpha","beta"]},"text":{"type":["string","null"]}},"oneOf":[{"properties":{"mode":{"const":"alpha"}},"required":["mode"]},{"properties":{"mode":{"const":"beta"}},"required":["mode"]}]}""")!.AsObject();
        Assert.True(PlanningContractCompatibility.Fits(actual, expected));
        expected["oneOf"]!.AsArray().Add(new JsonObject { ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } });
        Assert.False(PlanningContractCompatibility.Fits(actual, expected));
    }

}
