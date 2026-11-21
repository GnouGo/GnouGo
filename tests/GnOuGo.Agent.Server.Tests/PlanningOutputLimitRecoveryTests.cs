using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningOutputLimitRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderLengthStopsFreshDraftDespiteWarningsAndRestoresWithoutReplay(bool schemaValidText)
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var settings = new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = false, DiagnosticEnabled = true,
            MaxModelCalls = 1, MaxTotalTokens = 1, MaxActiveMilliseconds = 1 };
        var policy = new PlanningHostPolicy(fixture.Records, Options.Create(settings));
        using var model = new JournaledPlanningModel(new ExhaustedDraft(schemaValidText), preserveUsage: true);
        using var server = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model,
            settings, policy, DiagnosticPlanningTests.ModelOptions());
        var state = await server.StartAsync("output-exhaustion", "Return the required public numeric result named result with value 42.", false, Ct,
            thresholdMode: "diagnostic", diagnosticGrant: new("test-local-operator"));
        var capture = state.Request.Options.ToJsonString();
        for (var step = 0; step < 20 && !PlanningStatus.IsTerminal(state.Status) && !PlanningStatus.IsWaiting(state.Status); step++)
            state = await server.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("MODEL_OUTPUT_LIMIT", Assert.Single(state.Diagnostics).Code);
        Assert.NotEmpty(state.ThresholdWarnings!);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(capture), state.Request.Options));
        Assert.Null(state.PendingCall); Assert.Null(state.Construction!.Candidate);
        Assert.Empty(state.Construction.Checkpoints); Assert.Null(state.ApprovedHash); Assert.Null(state.Graph);
        Assert.Equal(0, state.ReplanAttempts);
        var request = Assert.Single(model.Requests, r => r.StructuredOutputSchema!["title"]!.ToString() == "construction_draft");
        Assert.Equal(8192, request.MaxTokens);
        var key = state.Request.SessionId + ":" + request.ClientRequestId;
        var receipt = (await fixture.Records.GetAsync("agent-planning-model-receipts-v11", "planning-tests", key, "test", Ct))!;
        var response = JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)!;
        Assert.Equal("output_limit", response.CompletionStatus);
        Assert.Equal(8192, response.Usage!["completion_tokens"]!.GetValue<int>());
        if (!schemaValidText)
            Assert.EndsWith(new string(' ', 24202), response.Raw!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        var count = model.Requests.Count;
        var serialized = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        using var cold = PlanningSessionLifecycleTests.Create(fixture, new HybridWorkflowPlanner(), PlanningSessionLifecycleTests.AgentCatalog(), model,
            settings, new PlanningHostPolicy(fixture.Records, Options.Create(settings)), DiagnosticPlanningTests.ModelOptions());
        var restored = (await cold.GetAsync(state.Request.SessionId, Ct))!;
        Assert.Equal(serialized, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
        restored = await cold.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = restored.Revision }, Ct);
        Assert.Equal(serialized, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(count, model.Requests.Count);
        Assert.Equal(receipt, await fixture.Records.GetAsync(receipt.Collection, "planning-tests", key, "test", Ct));
    }

    private sealed class ExhaustedDraft(bool schemaValidText) : ILLMClient
    {
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var response = await new GnOuGo.Planning.Examples.CurrentPlanningFixture.LiteralModel().CallAsync(request, ct);
            if (request.StructuredOutputSchema!["title"]!.ToString() != "construction_draft") return response;
            return new() { CompletionStatus = "output_limit", Text = schemaValidText ? response.Json!.ToJsonString() : "{\"items\":[" + new string(' ', 24202),
                Usage = new JsonObject { ["prompt_tokens"] = 120, ["completion_tokens"] = request.MaxTokens,
                    ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 320 }, ["total_tokens"] = 120 + request.MaxTokens } };
        }
    }
}
