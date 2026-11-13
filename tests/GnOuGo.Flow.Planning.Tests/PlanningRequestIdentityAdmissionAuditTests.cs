using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningRequestIdentityAdmissionAuditTests
{
    [Fact]
    public async Task CompleteRequestIdentityMustFitBeforeCreatingAPendingCall()
    {
        var state = PlannerFixture.Session();
        var prompt = new string('x', 2000);
        var schema = JsonNode.Parse("""{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}""")!.AsObject();
        var complete = PlanningModelCalls.PrepareRequest(state, PlanningConstruction.LocalPurpose, prompt, schema);
        state.Request.Generation.MaxInputTokensPerRequest = checked((int)PlanningRequestMeasurements.Measure(complete,
            PlanningConstruction.LocalPurpose, 4000).EstimatedInputTokens - 1);
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject { ["accepted"] = true } } };
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            PlanningConstruction.LocalPurpose, prompt, schema, PlannerFixture.Ct));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Equal("not_started", error.Details!["dispatch_status"]!.ToString());
        Assert.True(error.Details["current"]!.GetValue<long>() > state.Request.Generation.MaxInputTokensPerRequest);
        Assert.Empty(runtime.Calls);
        Assert.Equal(0, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
    }
}
