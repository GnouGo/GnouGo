using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LargeObservationGuidanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshSchemaKeepsIndependentExtractionSemanticsIncludingDomainFallback(bool fallback)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture("observed", "result");
        state.Requirements = runtime.Proposal.Requirements;
        if (fallback) state.Requirements!.Outputs = Enumerable.Range(0, 1001)
            .Select(i => new TaskInput { Name = "result_" + i, Type = new() }).ToList();
        var historical = PlanningSchemas.FullProposal(state).ToJsonString();
        var schema = PlanningSchemas.Proposal(state);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var description = string.Join('\n', PlanningSchemaReferences.Walk(schema, "", 0)
            .Select(s => s.Schema["description"]?.ToString()).OfType<string>());
        foreach (var meaning in new[] { "each.input: bound input", "one result per item", "ALL items", "bounded examples",
            "order/repeats", "global comparisons", "nested arrays" }) Assert.Contains(meaning, description);
        if (fallback) Assert.DoesNotContain("result_1000", schema.ToJsonString());
        Assert.Equal(historical, PlanningSchemas.FullProposal(state).ToJsonString());
        Assert.DoesNotContain("bounded examples", historical);
    }

    [Fact]
    public async Task IssuedPromptSeparatesConsumerInputsFromOutputAndIterationBounds()
    {
        var runtime = new TestRuntime();
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(0, state.ReplanAttempts);
        var request = Assert.Single(runtime.Calls);
        var instructions = GenericPlanningPromptTests.AssertGeneric(request);
        foreach (var meaning in new[] { "immediate consumer", "Input allowances", "output bounds", "iteration ceilings",
            "Pagination does not establish independence", "cross-item relationships", "original sources", "still too large" })
            Assert.Contains(meaning, instructions);
    }

    [Theory]
    [InlineData("request_input_allowance", "conservative_token_estimate")]
    [InlineData("mapping_request_admission", "serialized_utf8_bytes_plus_framing")]
    public async Task ExplicitRevisionContextKeepsBoundedMeasurementsWithoutGrantingAutomaticCorrection(string classification, string measurement)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.FailureEvidence = new JsonObject
        {
            ["error_code"] = "LLM_BUDGET_EXCEEDED", ["error_message"] = "PRIVATE_MESSAGE",
            ["details"] = new JsonObject { ["failed_step_id"] = "extract", ["classification"] = classification,
                ["dispatch_status"] = "not_started", ["limit_kind"] = "input_tokens", ["limit"] = 96000,
                ["current"] = 707090, ["measurement"] = measurement, ["host_limit"] = 96000,
                ["attempted_request_bound"] = 1200000L, ["smallest_complete_example_request_bound"] = null,
                ["sources"] = "PRIVATE_SNAPSHOT", ["script"] = "PRIVATE_SCRIPT" }
        };
        var before = state.Request.FailureEvidence.ToJsonString();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var context = GenericPlanningPromptTests.Context(Assert.Single(runtime.Calls))["executionFailure"]!;
        Assert.True(context["correction_blocked"]!.GetValue<bool>());
        Assert.Equal(96000L, context["limit"]!.GetValue<long>());
        Assert.Equal(707090L, context["current"]!.GetValue<long>());
        Assert.Equal(measurement, context["measurement"]!.ToString());
        Assert.Equal(1200000L, context["attempted_request_bound"]!.GetValue<long>());
        Assert.Equal("extract", context["failed_step_id"]!.ToString());
        Assert.DoesNotContain("PRIVATE_", runtime.Calls[0].Prompt);
        Assert.Equal(before, state.Request.FailureEvidence!.ToJsonString());
        var restored = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!
            .Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        Assert.True(JsonNode.DeepEquals(context, PlanningPrompt.FailureContext(restored.Request.FailureEvidence)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"PRIVATE_NUMBER\"")]
    [InlineData("9223372036854775808")]
    public void FailureMeasurementCannotSmugglePayloadsOrInvalidCounters(string invalid)
    {
        var evidence = JsonNode.Parse("{\"classification\":\"request_input_allowance\",\"dispatch_status\":\"not_started\",\"current\":" + invalid + "}")!.AsObject();
        var context = PlanningPrompt.FailureContext(evidence)!;
        Assert.True(context["correction_blocked"]!.GetValue<bool>());
        Assert.False(context.ContainsKey("current"));
    }
}
