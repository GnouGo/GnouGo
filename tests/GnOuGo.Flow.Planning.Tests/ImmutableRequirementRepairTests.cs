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
        var state = await Fixture(port);
        var blockers = TaskPlanRevisions.UnrepairableRequirements(state);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.RevisionScope, path => path.EndsWith("/outputs/message", StringComparison.Ordinal));
        var diagnostic = Assert.Single(blockers);
        Assert.Equal("/tasks/consume/requires", diagnostic.Location);
        Assert.Contains("observe", diagnostic.Message); Assert.Contains("ready", diagnostic.Message);
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var recovered = PlannerFixture.Clone(state);
        Assert.Equal(blockers, TaskPlanRevisions.UnrepairableRequirements(recovered));
        Assert.Equal(before, JsonSerializer.Serialize(recovered.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Fact]
    public async Task ProducerEditAuthorityDoesNotBecomeAnImmutableBlocker()
    {
        var state = await Fixture("unknown");
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
        var state = await Fixture("prêt", "prêt");
        state.Plan!.Root.Tasks[1].Outputs[0].Value.Port = "prêt";
        Assert.Empty(new TaskPlanCompiler().Compile(state.Plan, state.Catalog!).Diagnostics);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    private static async Task<PlanningSession> Fixture(string port, string declared = "ready")
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
        var state = PlannerFixture.Session(); state.Catalog = catalog; state.Plan = plan; state.Requirements = PlannerFixture.Requirements();
        state.Diagnostics = new TaskPlanCompiler().Compile(plan, catalog).Diagnostics.ToList();
        state.RevisionScope = TaskPlanRevisions.Scope(plan, state.Diagnostics).ToList();
        return state;
    }
}
