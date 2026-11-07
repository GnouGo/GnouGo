using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class DynamicMappingCollectionTests
{
    [Theory]
    [InlineData(12000, 96000, false)]
    [InlineData(96000, 12000, false)]
    [InlineData(12000, 96000, true)]
    [InlineData(96000, 12000, true)]
    public async Task TrustedAdvisoryRuntimeKeepsWholeExamplesAndDispatchesPastEstimatedThresholds(int hostLimit, int modelLimit, bool durable)
    {
        var model = new Model("item.label") { Allowance = modelLimit };
        var engine = AdaptiveEngine(model); engine.Limits.MaxMappingInputTokens = hostLimit;
        var journal = new InMemoryWorkflowRunStore();
        if (durable) { engine.RunStore = journal; engine.Limits.RunId = "trusted-advisory"; }
        engine.LLMUsageBudget = new LLMUsageBudgetScope(true, new() { MaxCalls = 1, MaxTotalTokens = 1 });
        var input = new JsonArray(new JsonObject { ["label"] = "first", ["text"] = new string('x', 24000) },
            new JsonObject { ["label"] = "second", ["text"] = new string('x', 24000) });
        var result = await AdaptiveRun(engine, input);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(new JsonArray("first", "second"), Result(result)));
        var request = Assert.Single(model.Requests);
        Assert.True(ModelRequestAdmission.EstimateTokens(request) > 12000 ||
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 4096 > 12000);
        var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        Assert.NotEmpty(data["examples"]!.AsArray());
        foreach (var example in data["examples"]!.AsArray())
            Assert.True(JsonNode.DeepEquals(input[example!["index"]!.GetValue<int>()], example["item"]));
        var usage = durable ? (await journal.ReadAsync("tenant", "trusted-advisory", Ct))!.ModelUsage! : engine.LLMUsageBudget.Snapshot;
        Assert.Equal(1, usage.Calls);
        Assert.True(usage.TotalTokens > 1);
        Assert.True(engine.LLMUsageBudget.AdvisoryTokenThresholds);
        Assert.Contains(usage.Warnings!, warning => warning.Stage == "mapping.dynamic" && warning.Kind == "estimated_input_bound" &&
            warning.Current > warning.Threshold && warning.RequestId == request.ClientRequestId);
        Assert.Contains(usage.Warnings!, warning => warning.Kind == "total_tokens" && warning.Current > 1 && warning.RequestId == request.ClientRequestId);
        var saved = JsonSerializer.Serialize(usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        Assert.DoesNotContain(new string('x', 30), saved, StringComparison.Ordinal);
        var restored = new LLMUsageBudgetScope(true, engine.LLMUsageBudget.Limits,
            JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(saved, JsonSerializer.Serialize(restored.Snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
    }

    [Theory]
    [InlineData(12000, 96000, "host")]
    [InlineData(96000, 12000, "model")]
    [InlineData(12000, 12000, "host_and_model")]
    public async Task CompleteExampleAdmissionExplainsTheEffectiveLimitWithoutLeakingData(int hostLimit, int modelLimit, string limitingFactor)
    {
        var model = new Model("item.label") { Allowance = modelLimit };
        var engine = AdaptiveEngine(model); engine.Limits.MaxMappingInputTokens = hostLimit;
        var telemetry = new WorkflowTelemetryTests.RecordingTelemetry(); engine.Telemetry = telemetry;
        var input = new JsonArray(new JsonObject { ["label"] = "PRIVATE_MARKER", ["text"] = new string('x', 20000) },
            new JsonObject { ["label"] = "PRIVATE_SECOND", ["text"] = new string('x', 24000) });
        var result = await AdaptiveRun(engine, input);
        Assert.False(result.Success); Assert.Empty(model.Requests); Assert.Equal("CONTRACT_UNSATISFIED", result.Error!.Code);
        var details = result.Error.Details!;
        Assert.Equal("mapping_request_admission", details["classification"]!.ToString());
        Assert.Equal("no_initial_example_fits", details["reason"]!.ToString());
        Assert.Equal("not_started", details["dispatch_status"]!.ToString());
        Assert.Equal(limitingFactor, details["limiting_factor"]!.ToString());
        Assert.Equal(hostLimit, details["host_limit"]!.GetValue<int>()); Assert.Equal(modelLimit, details["model_limit"]!.GetValue<int>());
        Assert.Equal(Math.Min(hostLimit, modelLimit), details["effective_limit"]!.GetValue<int>());
        Assert.Equal(4096, details["framing_allowance"]!.GetValue<int>()); Assert.Equal(2, details["examined_items"]!.GetValue<int>());
        Assert.True(details["empty_request_bound"]!.GetValue<long>() < 12000);
        Assert.True(details["smallest_complete_example_request_bound"]!.GetValue<long>() > 12000);
        Assert.DoesNotContain("PRIVATE", details.ToJsonString()); Assert.DoesNotContain(new string('x', 30), details.ToJsonString());
        var attributes = telemetry.StepSpans.Single(s => s.Name == "map").Attributes;
        Assert.Equal("no_initial_example_fits", attributes["gnougo.mapping.admission.outcome"]);
        Assert.Equal(2, attributes["gnougo.mapping.admission.examined_items"]);
    }

    [Fact]
    public async Task LargerConfiguredAllowanceKeepsCompleteUnicodeExamplesAndAllItems()
    {
        var model = new Model("item.label") { Allowance = 96000 };
        var engine = AdaptiveEngine(model); engine.Limits.MaxMappingInputTokens = 96000;
        var input = new JsonArray(new JsonObject { ["label"] = "é漢🙂\"\\\n", ["text"] = new string('x', 24000) },
            new JsonObject { ["label"] = "same\tvalue", ["text"] = new string('x', 24000) });
        var result = await AdaptiveRun(engine, input);
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(JsonNode.DeepEquals(new JsonArray(input[0]!["label"]!.DeepClone(), input[1]!["label"]!.DeepClone()), Result(result)));
        var request = Assert.Single(model.Requests);
        var measured = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) + 4096;
        Assert.InRange(measured, 12001, 96000);
        var data = JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!;
        foreach (var example in data["examples"]!.AsArray())
            Assert.True(JsonNode.DeepEquals(input[example!["index"]!.GetValue<int>()], example["item"]));
        Assert.Equal(1, engine.LLMUsageBudget!.Snapshot.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContextAndStillOversizedExamplesRemainBlocked(bool context)
    {
        var model = new Model("item.label") { Allowance = 96000 }; var engine = AdaptiveEngine(model);
        engine.Limits.MaxMappingInputTokens = 96000;
        var document = AdaptiveDocument();
        var input = new JsonArray(new JsonObject { ["label"] = context ? "small" : new string('z', 100000) });
        if (context) document.Workflows["main"].Steps[0].Input!["sources"]!["shared"] = new string('z', 100000);
        var result = await AdaptiveRun(engine, input, document);
        Assert.False(result.Success); Assert.Empty(model.Requests);
        Assert.Equal(context ? "context_too_large" : "no_initial_example_fits", result.Error!.Details!["reason"]!.ToString());
        Assert.Equal(context ? 0 : 1, result.Error.Details["examined_items"]!.GetValue<int>());
        if (context) Assert.Null(result.Error.Details["smallest_complete_example_request_bound"]);
        Assert.DoesNotContain(result.StepResults, s => s.StepId == "consumer");
    }
}
