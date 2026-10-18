using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class WorkflowPlanningRuntimeAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuppliedModelProvidesCapabilitiesBeforeTheEngineClient(bool engineHasAnotherClient)
    {
        var supplied = new Model(true);
        var other = new Model(false);
        var state = PlannerFixture.Session();
        var context = Context(new WorkflowEngine { LLMClient = engineHasAnotherClient ? other : null });
        var runtime = new WorkflowPlanningRuntime(context, supplied, (_, _) => Task.CompletedTask);

        var result = await PlanningModelCalls.CallAsync(state, runtime, "tasks", "Return the declared Boolean.", Schema(), PlannerFixture.Ct);

        Assert.True(result["accepted"]!.GetValue<bool>());
        Assert.Equal(1, supplied.Calls);
        Assert.Equal(0, other.Calls);
        Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
    }

    [Theory]
    [InlineData(false, "MODEL_REQUEST_INCOMPATIBLE")]
    [InlineData(null, ErrorCodes.LlmBudgetUnverifiable)]
    public async Task ExplicitEngineCapabilitiesRemainAuthoritative(bool? supported, string expectedCode)
    {
        var supplied = new Model(true);
        var state = PlannerFixture.Session();
        var runtime = new WorkflowPlanningRuntime(Context(new WorkflowEngine { LLMCapabilities = new Model(supported) }),
            supplied, (_, _) => Task.CompletedTask);

        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            "tasks", "Return the declared Boolean.", Schema(), PlannerFixture.Ct));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal("not_started", error.Details!["dispatch_status"]!.ToString());
        Assert.Equal(0, supplied.Calls);
        Assert.Equal(0, state.ModelCalls);
        Assert.Null(state.PendingCall);
    }

    private static JsonObject Schema() => JsonNode.Parse("""
        {"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}
        """)!.AsObject();

    private static StepExecutionContext Context(WorkflowEngine engine) => new()
    {
        Engine = engine, Data = new(), Limits = new(),
        Step = new() { Source = new() { Id = "planning", Type = "workflow.plan" } }
    };

    private sealed class Model(bool? supported) : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["accepted"] = true } });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult(supported);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
    }
}
