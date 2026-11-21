using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningPhaseJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LowPhaseAndItsEncryptedReceiptRemainExactDespiteGlobalMedium()
    {
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync();
        var policy = new PlanningConstructionPhasePolicy();
        policy.Navigation.Reasoning = "low"; policy.Navigation.OutputTokens = 4096;
        var planning = new PlanningRequest { TenantId = "tenant", SessionId = "phase-session",
            Generation = new() { Reasoning = "medium", MaxOutputTokens = 8192 },
            Options = new() { ["construction_limits"] =
                JsonSerializer.SerializeToNode(new PlanningConstructionOptions { PhasePolicy = policy }, PlanningJsonContext.Default.PlanningConstructionOptions) } };
        PlanningTransportPolicies.Capture(planning);
        var request = PlanningConstructionPhasePolicies.Apply(new()
        {
            Provider = "fixture", Model = "fixture", Prompt = "PRIVATE_PHASE_INTENT", RequiredReviewCalls = 2,
            StructuredOutputSchema = new JsonObject { ["title"] = "construction_select", ["type"] = "object" }
        }, planning, "construction_select");
        request.ClientRequestId = planning.SessionId + ":1:" + PlanningGraphCompiler.Fingerprint(
            JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        var exact = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
        Assert.Equal(2, request.RequiredReviewCalls);
        using var client = new Model(); var limits = new LLMUsageBudgetLimits { MaxCalls = 12 };
        var budget = new LLMUsageBudgetScope(limits, sink: new PlanningBudgetSink(fixture.Records, "tenant", planning.SessionId));
        var first = await new PlanningModelJournal(client, fixture, fixture.Records, "tenant", planning.SessionId,
            budget, new Estimator(), planning.Generation, planning: planning).CallAsync(request, Ct);
        Assert.Equal(exact, client.Issued);
        Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        var saved = (await fixture.Records.GetAsync(PlanningModelJournal.RequestCollection, "tenant",
            planning.SessionId + ":" + request.ClientRequestId, EfPlanningSessionStore.Author, Ct))!;
        Assert.Equal(exact, saved.Value);
        var usage = (await fixture.Records.GetAsync(PlanningBudgetSink.Collection, "tenant", planning.SessionId, EfPlanningSessionStore.Author, Ct))!;
        var restored = new LLMUsageBudgetScope(limits, JsonSerializer.Deserialize(usage.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Single(restored.Snapshot.JournaledAttempts!);
        var original = JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMRequest)!;
        var replay = await new PlanningModelJournal(client, fixture, fixture.Records, "tenant", planning.SessionId,
            restored, new Estimator(), planning.Generation, capabilities: new Unavailable(), planning: planning).CallAsync(original, Ct);
        Assert.Equal(JsonSerializer.Serialize(first, PlanningJsonContext.Default.LLMResponse), JsonSerializer.Serialize(replay, PlanningJsonContext.Default.LLMResponse));
        Assert.Equal(exact, JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(1, client.Calls); Assert.Equal(1, restored.Snapshot.Calls);
        Assert.Equal(10, restored.Snapshot.InputTokens); Assert.Equal(5, restored.Snapshot.OutputTokens);
        Assert.Equal(saved, await fixture.Records.GetAsync(PlanningModelJournal.RequestCollection, "tenant",
            planning.SessionId + ":" + request.ClientRequestId, EfPlanningSessionStore.Author, Ct));
        original.Reasoning = "high";
        var changed = JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest);
        await Assert.ThrowsAsync<PlanningConflictException>(() => new PlanningModelJournal(client, fixture, fixture.Records,
            "tenant", planning.SessionId, restored, new Estimator(), planning.Generation, planning: planning).CallAsync(original, Ct));
        Assert.Equal(changed, JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(1, client.Calls);
        foreach (var path in Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("PRIVATE_PHASE_INTENT", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, Ct)));
    }

    private sealed class Model : ILLMClient, ILLMCapabilityResolver, IDisposable
    {
        private readonly HttpClient _http = new(new Handler());
        private readonly RoutingLLMClientAdapter _client;
        public Model() => _client = new(new RoutingLLMClient(_http, new LLMOptions
        {
            DefaultProvider = "fixture", DefaultModel = "fixture",
            Models = { ["fixture"] = new() { Type = "openai", Url = "https://example.invalid/v1/chat/completions" } }
        }));
        internal int Calls;
        internal string? Issued;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++; Issued = JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
            Assert.Equal("low", request.Reasoning); Assert.Equal(4096, request.MaxTokens);
            return _client.CallAsync(request, ct);
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["low", "medium"]);
        public void Dispose() => _http.Dispose();
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                    """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"ok\":true}"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""") });
        }
    }
    private sealed class Unavailable : ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => throw new InvalidOperationException("Receipt recovery must not repeat admission.");
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("Receipt recovery must not repeat admission.");
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("Receipt recovery must not repeat admission.");
    }
    private sealed class Estimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0.001m;
    }
}
