using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ImmutableRequirementRepairTests
{
    // Sanitized corrupt references retained from the live response; arbitrary producer names.
    [Theory]
    [InlineData("success兑},{")]
    [InlineData("successд},{")]
    public async Task MixedEditableErrorsDoNotSpendRepairsOnAnImmutableInvalidPort(string port)
    {
        var (runtime, state) = await Fixture(port);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.RevisionScope, p => p.EndsWith("/outputs/message", StringComparison.Ordinal));
        var diagnostic = Assert.Single(state.Diagnostics, d => d.Code == "REVISION_REQUIRED");
        Assert.Equal("/tasks/consume/requires", diagnostic.Location);
        Assert.Contains("observe", diagnostic.Message); Assert.Contains("ready", diagnostic.Message);
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var recovered = PlannerFixture.Clone(state);
        recovered = await planner.AdvanceAsync(recovered, new() { ExpectedRevision = recovered.Revision }, runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls); Assert.Equal(before, JsonSerializer.Serialize(recovered.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Fact]
    public async Task ProducerEditAuthorityDoesNotBecomeAnImmutableBlocker()
    {
        var (runtime, state) = await Fixture("unknown");
        state.Plan = runtime.Proposal.Plan;
        state.Diagnostics = [new("TASK_OUTPUT_UNKNOWN", "/tasks/consume/requires", "Missing port")];
        state.RevisionScope = ["/tasks/observe/operation"];
        Assert.Empty(TaskPlanRevisions.UnrepairableRequirements(state));
        state.RevisionScope = ["/tasks/observe/inputs/output_schema"];
        Assert.Empty(TaskPlanRevisions.UnrepairableRequirements(state));
        state.RevisionScope = ["/tasks/consume/outputs/message"];
        Assert.Single(TaskPlanRevisions.UnrepairableRequirements(state));
    }

    [Fact]
    public async Task DeclaredUnicodePortsAreNotSanitizedOrRejected()
    {
        var (runtime, state) = await Fixture("prêt", "prêt");
        runtime.Proposal.Plan!.Root.Tasks[1].Outputs[0].Value.Port = "prêt";
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
    }

    private static async Task<(TestRuntime, PlanningSession)> Fixture(string port, string declared = "ready")
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "observe_flag", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [declared] = new JsonObject { ["type"] = "boolean" } },
                ["required"] = new JsonArray(declared), ["additionalProperties"] = false } }] });
        var runtime = new TestRuntime(new() { McpClientFactory = factory });
        var catalog = await TaskPlanCompilerTests.Catalog(runtime.Actual);
        var plan = new TaskPlan { Root = new() { Tasks = [
            new() { Id = "observe", Objective = "Observe readiness", Operation = catalog.Capabilities.Single(c => c.Method == "observe_flag").Id },
            new() { Id = "consume", Kind = "value", Objective = "Return the observed flag when ready",
                Requires = new() { Kind = "output", Source = "observe", Port = port },
                Outputs = [new("message", new() { Kind = "output", Source = "observe", Port = "other_invalid_port" })] }],
            Outputs = [new("message", new() { Kind = "output", Source = "consume", Port = "message" })] } };
        runtime.Proposal = new() { Plan = plan, Requirements = PlannerFixture.Requirements() };
        var state = PlannerFixture.Session(); state.Catalog = catalog;
        return (runtime, state);
    }
}
