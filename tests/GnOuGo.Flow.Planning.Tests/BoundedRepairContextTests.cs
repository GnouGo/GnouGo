using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BoundedRepairContextTests
{
    private static PlanningSession State()
    {
        var (plan, catalog) = ConstrainedBindingTests.Retained();
        var state = PlannerFixture.Session(); state.Plan = plan; state.Catalog = catalog;
        state.Requirements = PlannerFixture.Requirements(); state.ModelCalls = 3; state.Status = PlanningStatus.Generating;
        state.Request.Generation.MaxInputTokensPerRequest = 60397; state.Request.Generation.MaxOutputTokens = 32768;
        state.Usage = new() { Calls = 3, TotalTokens = 81391, EstimatedCost = 0.54m, EstimatedCostCurrency = "EUR" };
        // Recover the old four-slot scope, before producer constraints were expressible.
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics.Where(d => d.Code == "TASK_INPUT_TYPE").ToList();
        state.RevisionScope = state.Diagnostics.Select(d => d.Location).ToList();
        state.Discovery.Sources = [new("selected", "Selected operations"), new("unseen", "Uninspected source")];
        var summaries = catalog.Capabilities.Select(c => new CapabilitySummary(c.Id, "selected", c.Method!, c.Description,
            c.StepType, c.EffectKind, c.Version, Operation: TaskOperations.Describe(c))).ToList();
        summaries.AddRange(Enumerable.Range(0, 95).Select(i => new CapabilitySummary("unused-" + i, "selected", "unused-" + i,
            "Unrelated operation", "mcp.call", "read", "v1", Operation: new() { Id = "unused-" + i, Version = "v1",
                Description = string.Concat(Enumerable.Repeat("Complete unrelated metadata must stay cached. ", 70)) })));
        state.Discovery.Pages = [new("selected", null, summaries, "next-page")];
        state.Discovery.Limitations = ["Discovery is incomplete: 1 sources were not inspected.", "Additional operation pages remain uninspected."];
        state.Discovery.Resolved = catalog.Capabilities.ToList();
        return state;
    }

    [Fact]
    public async Task BindingRepairUsesCompleteSelectedContractsAndRetainsCachedDiscovery()
    {
        var state = State(); var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var usage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var unbounded = PlannerFixture.Clone(state); unbounded.RevisionScope.Clear();
        Assert.True(PlanningJsonTransport.EstimateInputTokens(HybridWorkflowPlanner.Prompt(unbounded), PlanningSchemas.Proposal(unbounded)) > 60397);
        var runtime = new TestRuntime { Proposal = new() { Plan = ConstrainedBindingTests.Repair(state.Plan!) } };
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var request = Assert.Single(runtime.Calls); var context = Context(request.Prompt);
        Assert.True(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema!.AsObject()) < 24000);
        Assert.Equal(3, context["pages"]![0]!["operations"]!.AsArray().Count);
        foreach (var operation in context["pages"]![0]!["operations"]!.AsArray())
        {
            var original = state.Discovery.Pages[0].Capabilities.Single(c => c.Id == operation!["id"]!.ToString()).Operation!;
            Assert.Equal(original.Description, operation!["description"]!.ToString());
            Assert.Equal(original.Inputs.Count, operation["inputs"]!.AsArray().Count);
            foreach (var input in operation["inputs"]!.AsArray())
                Assert.True(JsonNode.DeepEquals(original.Inputs.Single(p => p.Name == input!["name"]!.ToString()).Schema, input!["type"]));
        }
        Assert.Equal("null", request.StructuredOutputSchema["properties"]!["discoveryRequests"]!["type"]!.ToString());
        Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(usage, JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(4, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Equal(0, runtime.Discoveries);
        Assert.StartsWith(state.Request.SessionId + ":4:", request.ClientRequestId);
        Assert.Equal(7, context["revisionScope"]!.AsArray().Count);
    }

    [Fact]
    public async Task RejectedRecoveredRepairPreservesBaselineReceiptsAndBudget()
    {
        var state = State(); var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var runtime = new TestRuntime { Proposal = new() { Plan = ConstrainedBindingTests.Repair(state.Plan!) } };
        runtime.Proposal.Plan.Root.Tasks[0].Objective = "Unrelated edit";
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/tasks/parse_pr_url/objective");
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(81391, state.Usage!.TotalTokens); Assert.Equal(4, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        var identity = runtime.Calls[0].ClientRequestId;
        runtime.Proposal.Plan = ConstrainedBindingTests.Repair(state.Plan!);
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(5, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts); Assert.Equal(identity, runtime.Calls[0].ClientRequestId);
    }

    [Fact]
    public async Task PendingRequestRetainsItsSchemaIdentityAndIssuedScope()
    {
        var state = State(); var runtime = new TestRuntime { Proposal = new() { Plan = ConstrainedBindingTests.Repair(state.Plan!) } };
        var schema = PlanningSchemas.Proposal(state); schema["description"] = "Original persisted request";
        state.PendingCall = new() { Id = "retained", Purpose = "replan", Request = new() { ClientRequestId = "retained", Prompt = "Original retained prompt", StructuredOutputSchema = schema } };
        state.Request.Generation.MaxInputTokensPerRequest = 1024;
        var scope = state.RevisionScope.ToArray(); var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal("retained", Assert.Single(runtime.Calls).ClientRequestId);
        Assert.True(JsonNode.DeepEquals(schema, runtime.Calls[0].StructuredOutputSchema));
        Assert.Equal("Original retained prompt", runtime.Calls[0].Prompt);
        Assert.Equal(scope, state.RevisionScope); Assert.Equal(3, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task SelectedContextStillHonorsTheSavedCeilingAndOperationRepairsKeepAlternatives()
    {
        var state = State(); state.Request.Generation.MaxInputTokensPerRequest = 1024;
        var runtime = new TestRuntime();
        var stopped = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Empty(runtime.Calls); Assert.Equal(3, stopped.ModelCalls); Assert.Equal(0, stopped.ReplanAttempts);
        Assert.Equal(1024, stopped.Request.Generation.MaxInputTokensPerRequest);
        Assert.Contains(stopped.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT");
        state.RevisionScope.Add("/tasks/submit_review_decision/operation");
        Assert.Equal(98, Context(HybridWorkflowPlanner.Prompt(state))["pages"]![0]!["operations"]!.AsArray().Count);
        Assert.Null(PlanningSchemas.Proposal(state)["properties"]!["discoveryRequests"]!["type"]);
    }

    [Fact]
    public async Task CompactBaselinePreservesAllSemanticValuesAndMalformedNondefaults()
    {
        var plans = new List<TaskPlan> { ConstrainedBindingTests.Retained().Plan, ConstrainedBindingTests.Repair(ConstrainedBindingTests.Retained().Plan) };
        foreach (var name in PlanningCorpus.Names)
        {
            var environment = new PlanningBenchmarkCases.Environment(name);
            var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { McpClientFactory = environment.Factory() }, (_, _) => Task.CompletedTask);
            plans.Add(PlanningCorpus.Tasks(name, await TaskPlanCompilerTests.Catalog(runtime)));
        }
        foreach (var plan in plans)
        {
            var full = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!;
            var compact = PlanningJsonTransport.TaskPlanPrompt(plan)!;
            var restored = compact.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
            Assert.True(JsonNode.DeepEquals(full, JsonSerializer.SerializeToNode(restored, PlanningJsonContext.Default.TaskPlan)));
        }
        var retained = ConstrainedBindingTests.Retained().Plan;
        Assert.True(Encoding.UTF8.GetByteCount(PlanningJsonTransport.Prompt(PlanningJsonTransport.TaskPlanPrompt(retained)!)) <
            Encoding.UTF8.GetByteCount(PlanningJsonTransport.Prompt(JsonSerializer.SerializeToNode(retained, PlanningJsonContext.Default.TaskPlan)!)) * 0.7);
        retained.Root.Tasks[0].MaxItems = 123; retained.Root.Tasks[0].Inputs[0].Value.Items.Add(new() { Kind = "boolean", Boolean = false });
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(retained, PlanningJsonContext.Default.TaskPlan),
            JsonSerializer.SerializeToNode(PlanningJsonTransport.TaskPlanPrompt(retained)!.Deserialize(PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)));
    }

    private static JsonNode Context(string prompt) => JsonNode.Parse(prompt[(prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
}
