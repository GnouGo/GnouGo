using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InvalidResponseProgressTests
{
    [Fact]
    public async Task RepeatedInvalidResponseRetainsDistinctAttemptsAcrossRestart()
    {
        var state = PlannerFixture.Session();
        var runtime = InvalidRuntime();
        await Invalid(state, runtime, Schema());
        state = PlannerFixture.Clone(state);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Call(state, runtime, Schema()));
        Assert.Equal("REPLAN_NO_PROGRESS", error.Code);
        var attempts = PlannerFixture.Clone(state).StrategyAttempts!;
        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0]["requestId"]!.ToString(), attempts[1]["requestId"]!.ToString());
        Assert.Equal(attempts[0]["usefulState"]!.ToString(), attempts[1]["usefulState"]!.ToString());
        Assert.Equal(attempts[0]["schemaHash"]!.ToString(), attempts[1]["schemaHash"]!.ToString());
        Assert.True(JsonNode.DeepEquals(attempts[0], error.Details!["previousAttempt"]));
        Assert.True(JsonNode.DeepEquals(attempts[1], error.Details!["currentAttempt"]));
        Assert.Equal(2, state.ModelCalls);
        Assert.Null(state.Construction!.Candidate);
        Assert.All(state.RequestMeasurements!, measurement => Assert.Equal("invalid_response", measurement.Result));
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("schema")]
    public async Task SameInvalidResponseOnChangedUsefulBasisIsANewRejectedAttempt(string change)
    {
        var state = PlannerFixture.Session();
        var runtime = InvalidRuntime();
        await Invalid(state, runtime, Schema());
        state = PlannerFixture.Clone(state);
        var schema = Schema();
        if (change == "evidence") (state.Construction!.Evidence ??= new())["source:new"] = "Exact newly retrieved original fact";
        else schema["properties"]!["value"]!["type"] = "integer";
        await Invalid(state, runtime, schema);
        Assert.Equal(2, state.StrategyAttempts!.Count);
        Assert.False(state.StrategyAttempts[1]["repeated"]!.GetValue<bool>());
        Assert.NotEqual(state.StrategyAttempts[0]["identity"]!.ToString(), state.StrategyAttempts[1]["identity"]!.ToString());
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Call(state, runtime, schema));
        Assert.Equal("REPLAN_NO_PROGRESS", error.Code);
        Assert.Equal(state.StrategyAttempts[1]["requestId"]!.ToString(), error.Details!["previousAttempt"]!["requestId"]!.ToString());
        Assert.Equal(3, state.ModelCalls);
        Assert.Null(state.Construction!.Candidate);
    }

    [Fact]
    public async Task ExactReceivedCallRecoveryDoesNotInventAnotherInvalidAttempt()
    {
        var state = PlannerFixture.Session();
        var runtime = InvalidRuntime();
        await Invalid(state, runtime, Schema());
        var issued = PlannerFixture.Clone(runtime.Checkpoints.Single()).PendingCall!;
        state = PlannerFixture.Clone(state);
        state.PendingCall = issued;
        await Invalid(state, runtime, Schema());
        Assert.Single(state.StrategyAttempts!);
        Assert.Equal(1, state.ModelCalls);
        Assert.Single(state.RequestMeasurements!);
        Assert.Equal(issued.Id, state.StrategyAttempts![0]["requestId"]!.ToString());
        Assert.False(state.StrategyAttempts[0]["repeated"]!.GetValue<bool>());
    }

    [Fact]
    public void RecoveredRepeatedDiscoveryRetainsItsOriginalPriorAttempt()
    {
        var state = PlannerFixture.Session();
        var response = new JsonObject { ["evidenceRequests"] = new JsonArray("description:operation") };
        PlanningProgress.AssessAttempt(state, PlanningCompactConstruction.DiscoveryPurpose, response, "first");
        var initial = Assert.Throws<WorkflowRuntimeException>(() => PlanningProgress.AssessAttempt(state,
            PlanningCompactConstruction.DiscoveryPurpose, response, "second", suffix: "description-read"));
        state = PlannerFixture.Clone(state);
        var recovered = Assert.Throws<WorkflowRuntimeException>(() => PlanningProgress.AssessAttempt(state,
            PlanningCompactConstruction.DiscoveryPurpose, response, "second", suffix: "description-read"));
        Assert.Equal("first", recovered.Details!["previousAttempt"]!["requestId"]!.ToString());
        Assert.True(JsonNode.DeepEquals(initial.Details, recovered.Details));
        Assert.Equal(2, state.StrategyAttempts!.Count);
    }

    [Fact]
    public async Task RepairPhaseAnnotationsDoNotHideAnUnchangedInvalidAttempt()
    {
        var state = PlannerFixture.Session();
        var runtime = InvalidRuntime();
        await Invalid(state, runtime, Schema());
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime,
            "replan", "Correct the rejected response.", Schema(), PlannerFixture.Ct));
        Assert.Equal("REPLAN_NO_PROGRESS", error.Code);
        Assert.Equal(state.StrategyAttempts![0]["schemaHash"]!.ToString(), state.StrategyAttempts[1]["schemaHash"]!.ToString());
        Assert.Equal(1, state.ReplanAttempts);
    }

    [Fact]
    public async Task MalformedStrategyFieldsStillReceiveSchemaDiagnostics()
    {
        var state = PlannerFixture.Session();
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = JsonNode.Parse("""
            {"extension":{"groups":[3],"tasks":[{"id":"x","dependsOn":[null]}]},"operationIds":[null],"closeScopes":[null]}
            """) } };
        await Invalid(state, runtime, Schema());
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Call(state, runtime, Schema()));
        Assert.Equal("REPLAN_NO_PROGRESS", error.Code);
        Assert.Equal(2, state.StrategyAttempts!.Count);
    }

    private static TestRuntime InvalidRuntime() => new() { Respond = (_, _) => new() { Json = new JsonObject { ["value"] = false } } };
    private static JsonObject Schema() => PlanningSchemas.Object(("value", PlanningSchemas.String()));
    private static Task<JsonNode> Call(PlanningSession state, TestRuntime runtime, JsonObject schema) =>
        PlanningModelCalls.CallAsync(state, runtime, PlanningConstruction.DraftPurpose, "Produce the requested response.", schema, PlannerFixture.Ct);
    private static async Task Invalid(PlanningSession state, TestRuntime runtime, JsonObject schema)
    {
        var error = await Assert.ThrowsAsync<PlanningResponseException>(() => Call(state, runtime, schema));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "PLANNING_RESPONSE_INVALID");
    }
}
