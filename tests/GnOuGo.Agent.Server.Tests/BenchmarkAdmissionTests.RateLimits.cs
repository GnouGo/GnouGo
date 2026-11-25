using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Tests.Shared;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkAdmissionTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", 0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData(" { \"note\": \"privé 🧪\" }\r\n", 28, "48b9bd012ea75bb7c881428530034b50ffe78aa9754ac5e829e01cc76e9ae970")]
    public async Task RequestInspectionRetainsHistoricalUncertaintyAndReadsOnlyItsCampaign(string? body, int? bytes, string? bodyHash)
    {
        using var fixture = new Fixture();
        var campaign = fixture.Campaign;
        const string requestId = "session-copilot:request";
        var wireHash = new string('a', 64);
        var original = new JsonObject
        {
            ["input_ceiling"] = 12L, ["output_ceiling"] = 8L, ["cost_ceiling_eur"] = 3.25m,
            ["transport"] = new JsonObject { ["Fingerprint"] = wireHash,
                ["Attempts"] = new JsonArray(new JsonObject { ["Id"] = requestId }) }
        };
        if (body is not null) original["transport"]!["RequestBody"] = body;
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, requestId, new JsonObject(), Ct);
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, requestId, original, Ct);
        var stored = await fixture.Records.GetAsync(BenchmarkHttpJournal.Collection, "benchmark", campaign.Id + ":" + requestId, BenchmarkCampaign.Author, Ct);
        var before = fixture.Records.MutationAttempts;
        var evidence = await BenchmarkHttpJournal.InspectRequestAsync(campaign, requestId, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BenchmarkHttpJournal.InspectRequestAsync(new(fixture.Records, "other"), requestId, Ct));
        Assert.Equal(wireHash, evidence["wire_sha256"]!.ToString());
        Assert.True(stored!.UpdatedAt > stored.CreatedAt);
        Assert.Equal(stored.CreatedAt.ToUniversalTime(), evidence["journal_created_utc"]?.GetValue<DateTimeOffset>());
        Assert.Equal(stored.UpdatedAt.ToUniversalTime(), evidence["journal_updated_utc"]?.GetValue<DateTimeOffset>());
        Assert.Equal(bytes, evidence["request_body_utf8_bytes"]?.GetValue<int>());
        Assert.Equal(bodyHash, evidence["request_body_sha256"]?.ToString());
        Assert.DoesNotContain("priv", evidence.ToJsonString());
        Assert.Equal(1L, evidence["unknown_usage_attempts"]!.GetValue<long>());
        Assert.Equal(3.25m, evidence["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Null(evidence["measured_cost_eur"]);
        var attempt = Assert.Single(evidence["attempts"]!.AsArray())!;
        Assert.Null(attempt["started_utc"]);
        Assert.Null(attempt["status"]);
        Assert.Null(attempt["requested_delay_ms"]);
        Assert.False(attempt["response_id_available"]!.GetValue<bool>());
        Assert.Equal(before, fixture.Records.MutationAttempts);
        Assert.True(JsonNode.DeepEquals(original, await campaign.LoadAsync(BenchmarkHttpJournal.Collection, requestId, Ct)));
    }

    [Theory]
    [InlineData(false, 429)]
    [InlineData(true, 429)]
    [InlineData(false, 503)]
    [InlineData(true, 503)]
    public async Task RuntimeRetriesOneConfirmedRejectionAndRetainsBothPhysicalReservations(bool sdk, int status)
    {
        using var fixture = new Fixture();
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        var wait = TimeSpan.FromMilliseconds(10);
        using var handler = new RetryProvider(clock, status);
        using var http = new HttpClient(handler);
        using var model = RuntimeModel(fixture, RuntimeOptions(), http, clock);
        var run = InvokeRuntime(model, sdk);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        while (!clock.HasTimer(wait) && !run.IsCompleted) await Task.Delay(1, limit.Token);
        clock.Advance(wait - TimeSpan.FromTicks(1));
        Assert.Single(handler.Ids);
        clock.Advance(TimeSpan.FromTicks(1));
        await run.WaitAsync(limit.Token);
        Assert.Equal([start, start + wait], handler.Times);
        var row = (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, RuntimeId(sdk), Ct))!;
        var attempts = row["transport"]!["Attempts"]!.AsArray();
        Assert.Equal(new[] { status, 200 }, attempts.Select(a => a!["Status"]!.GetValue<int>()));
        Assert.Equal(2, handler.Ids.Distinct().Count());
        Assert.Equal(handler.Ids, attempts.Select(a => a!["Id"]!.ToString()));
        Assert.Equal("retry-after-ms", attempts[0]!["ProviderRetryDelay"]!["Header"]!.ToString());
        var original = row.ToJsonString();
        await InvokeRuntime(model, sdk);
        Assert.Equal(2, handler.Ids.Count);
        Assert.Equal(original, (await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, RuntimeId(sdk), Ct))!.ToJsonString());
        var totals = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        Assert.Equal(2L, totals["calls"]!.GetValue<long>());
        Assert.Equal(1L, totals["unknown_attempts"]!.GetValue<long>());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task RuntimeAttemptReservationsRespectTheWorkflowAndParentCallCeilings(int ceiling, bool monetary)
    {
        using var fixture = new Fixture();
        using var upstream = new RetryProvider(TimeProvider.System, 503);
        using var http = new HttpClient(upstream);
        var options = RuntimeOptions();
        options.ModelOverrides["openai/pinned"].Capabilities = new() { SupportsReasoningEffort = true, SupportedReasoningEfforts = ["medium"] };
        using var model = RuntimeModel(fixture, options, http, TimeProvider.System);
        var parent = new LLMUsageBudgetScope(new LLMUsageBudgetLimits
            { MaxCalls = monetary ? 4 : ceiling, MaxEstimatedCostUsd = monetary ? 0.15m : null });
        var child = parent.CreateChild(new() { MaxCalls = 4 });
        var client = new LiveWorkflowEvaluation.ExecutionModel(model, "scoped");
        var request = new LLMRequest { Provider = "fixture", Model = "pinned", Prompt = "Read only.", ClientRequestId = "one", MaxTokens = 128 };
        Task<LLMResponse> Send() => child.CallAsync(client, new Price(), request, "llm.call", Ct);
        if (ceiling == 1)
        {
            var denied = await Assert.ThrowsAsync<WorkflowRuntimeException>(Send);
            Assert.Equal("prior_attempts_recorded", denied.Details!["dispatch_status"]!.ToString());
            Assert.False(denied.Details["next_attempt_started"]!.GetValue<bool>());
            Assert.Equal(503, denied.Details["status_code"]!.GetValue<int>());
        }
        else
        {
            Assert.NotNull((await Send()).Usage);
            Assert.Equal(ceiling, parent.Snapshot.Calls);
            var original = parent.Snapshot;
            Assert.Null(original.JournaledAttempts![upstream.Ids[0]].VerifiedUsage);
            Assert.NotNull(original.JournaledAttempts[upstream.Ids[1]].VerifiedUsage);
            var codec = PlanningJsonContext.Default.LLMUsageBudgetSnapshot;
            var encoded = JsonSerializer.Serialize(original, codec);
            var restoredParent = new LLMUsageBudgetScope(parent.Limits, JsonSerializer.Deserialize(encoded, codec));
            var restoredChild = new LLMUsageBudgetScope(child.Limits,
                JsonSerializer.Deserialize(JsonSerializer.Serialize(child.Snapshot, codec), codec), restoredParent);
            using var reopened = RuntimeModel(fixture, options, http, TimeProvider.System);
            await restoredChild.CallAsync(new LiveWorkflowEvaluation.ExecutionModel(reopened, "scoped"), new Price(), request, "llm.call", Ct);
            Assert.Equal(encoded, JsonSerializer.Serialize(restoredParent.Snapshot, codec));
            Assert.Equal(ceiling, restoredChild.Snapshot.Calls);
        }
        Assert.Equal(ceiling, upstream.Ids.Count);
        Assert.Equal(ceiling, parent.Snapshot.Calls);
        Assert.Equal(ceiling, child.Snapshot.Calls);
        var accounting = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        Assert.Equal(ceiling, accounting["calls"]!.GetValue<long>());
    }

    [Fact]
    public async Task LocalRelayReturnsValidatedRetryHeadersAndPreservesTheSingleCreditOfAnOlderClient()
    {
        using var fixture = new Fixture();
        using var upstream = new RetryProvider(TimeProvider.System, 429);
        using var transport = new HttpClient(upstream);
        using var model = RuntimeModel(fixture, RuntimeOptions(), transport);
        await using var relay = await CampaignInferenceProxy.StartAsync(model, "legacy");
        using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
        var endpoint = new Uri(relay.Endpoint);
        var backend = new GnOuGo.AI.Core.McpServerOptions { EnvironmentVariables = new() { ["Code__Copilot__InferenceProxyEndpoint"] = relay.Endpoint } };
        var servers = new Dictionary<string, GnOuGo.AI.Core.McpServerOptions> { [AdaptiveLiveProfile.Backend] = backend };
        Assert.Throws<GnOuGo.Flow.Core.Planning.PlanningConflictException>(() => relay.RequireSdkAdmission(servers));
        await new CopilotInferenceProxyHandler(local, endpoint).VerifyReadyAsync(Ct);
        relay.RequireSdkAdmission(servers);
        backend.EnvironmentVariables["Code__Copilot__InferenceProxyEndpoint"] = relay.Endpoint + "/another";
        Assert.Throws<GnOuGo.Flow.Core.Planning.PlanningConflictException>(() => relay.RequireSdkAdmission(servers));
        relay.ExecutionEnabled = true;
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("""{"model":"pinned","messages":[{"role":"user","content":"fixture"}]}""", Encoding.UTF8, "application/json")
        };
        message.Headers.Add(CopilotInferenceProxyHandler.RequestHeader, "one");
        message.Headers.Add(CopilotInferenceProxyHandler.UpstreamHeader, "https://example.invalid/v1/chat/completions");
        using var response = await local.SendAsync(message, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("10", response.Headers.GetValues("retry-after-ms").Single());
        var diagnostic = JsonSerializer.Deserialize(response.Headers.GetValues(CopilotInferenceFailure.Header).Single(), CopilotCoreJsonContext.Default.CopilotInferenceFailure)!;
        Assert.Equal("RateLimited", diagnostic.Classification);
        Assert.Equal(10, diagnostic.RequestedDelayMilliseconds);
        Assert.Equal(1, diagnostic.Attempts);
        Assert.Equal("provider_rejection", diagnostic.Phase);
        Assert.Equal(429, diagnostic.Status);
        Assert.Single(upstream.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalSdkReceivesRetryCreditAfterAToolAndRunsItOnlyOnce(bool deferred)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GNOUGO_TEST_LOCAL_COPILOT") == "1", "Opt in to the bundled local Copilot runtime.");
        using var fixture = new Fixture();
        var options = RuntimeOptions();
        options.Models["fixture"].Url = "http://127.0.0.1:1/v1";
        using var handler = new RetryProvider(TimeProvider.System, 429, tool: true, waitMs: deferred ? "90000" : "10");
        using var transport = new HttpClient(handler);
        using var model = RuntimeModel(fixture, options, transport, TimeProvider.System);
        await using var relay = await CampaignInferenceProxy.StartAsync(model, "offline");
        relay.ExecutionEnabled = true;
        using var local = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var proxy = new CopilotInferenceProxyHandler(local, new Uri(relay.Endpoint));
        await proxy.VerifyReadyAsync(Ct);
        const string tool = "fixture_observe", prompt = "Read the fixture with its tool, then return the result.";
        List<string> effects = [];
        List<int> charges = [];
        var bounds = new CopilotExecutionBounds(4, 250000, DateTimeOffset.UtcNow.AddMinutes(2), new HashSet<string> { tool },
            (count, _, _) => { charges.Add(count); return Task.CompletedTask; })
        {
            ApprovedPrompt = prompt, MaxActions = 1, AdaptiveActions = new((_, _) => Task.CompletedTask, new HashSet<string>())
        };
        var config = new CopilotRuntimeConfiguration(fixture.Root, "pinned", AvailableTools: [tool],
            Environment: new Dictionary<string, string> { ["COPILOT_HOME"] = fixture.Root, ["COPILOT_TELEMETRY"] = "false",
                ["TMP"] = fixture.Root, ["TEMP"] = fixture.Root })
        {
            ExecutionBounds = bounds, HostTools = [new(tool, "Read fixture", JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement,
                (_, _) => { effects.Add("observed"); return Task.FromResult(JsonSerializer.SerializeToElement("observed")); })]
        };
        var context = new CopilotRequestContext("fixture", RunId: "offline");
        await using var client = new GitHubCopilotSdkClientFactory(null, proxy).Create(config);
        await client.StartAsync(Ct);
        var provider = new CopilotProviderResolution("fixture", "pinned", new() { Type = "openai", WireApi = "completions",
            BaseUrl = options.Models["fixture"].Url, ApiKey = "offline-fixture", ModelId = "pinned" });
        await using var session = await client.CreateSessionAsync(new(new(context, config), provider, new FixturePermission()), Ct);
        Task<CopilotSendResult> Send() => session.SendAsync(session.SessionId, new(context, session.SessionId, prompt, TimeoutSeconds: 45), Ct);
        if (deferred)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var incident = await Assert.ThrowsAsync<CopilotSendInterruptedException>(() => Send().WaitAsync(deadline.Token));
            var failure = Assert.IsType<CopilotInferenceFailure>(incident.Snapshot.InferenceFailure);
            Assert.Equal(429, failure.Status);
            Assert.Equal(90000, failure.RequestedDelayMilliseconds);
            Assert.NotNull(failure.NotBeforeUtc);
            Assert.Equal("RateLimited", failure.Classification);
            Assert.Equal(1, failure.Attempts);
            Assert.Equal(["observed"], effects);
            Assert.Equal([1, 2], charges);
            Assert.Equal(2, handler.Ids.Count);
            return;
        }
        var answer = await Send();
        Assert.True(answer.Completed);
        Assert.Equal("observed", answer.Content.Trim());
        Assert.Equal(["observed"], effects);
        Assert.Equal([1, 2, 3], charges);
        Assert.Equal(3, handler.Ids.Distinct().Count());
        Assert.Equal(handler.Bodies[1], handler.Bodies[2]);
        var totals = await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct);
        Assert.Equal(3L, totals["calls"]!.GetValue<long>());
    }

    private sealed class FixturePermission : ICopilotHumanInputProvider
    {
        public Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken ct)
            => Task.FromResult(new CopilotHumanInputResponse(request.RequestedHostTool == "fixture_observe", "Allow once"));
    }

    private sealed class RetryProvider(TimeProvider clock, int status, bool tool = false, string waitMs = "10") : HttpMessageHandler
    {
        internal List<DateTimeOffset> Times { get; } = [];
        internal List<string> Ids { get; } = [];
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Times.Add(clock.GetUtcNow());
            Ids.Add(request.Headers.GetValues("X-Client-Request-Id").Single());
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            if (Ids.Count == (tool ? 2 : 1))
            {
                var rejected = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("""{"error":{"code":"rate_limit_exceeded"}}""") };
                rejected.Headers.TryAddWithoutValidation("retry-after-ms", waitMs);
                return rejected;
            }
            var selecting = tool && Ids.Count == 1;
            var message = selecting ? """{"role":"assistant","tool_calls":[{"index":0,"id":"call_fixture","type":"function","function":{"name":"fixture_observe","arguments":"{}"}}]}"""
                : """{"role":"assistant","content":"observed"}""";
            return ChatReply(JsonNode.Parse(Bodies[^1])!, JsonNode.Parse(message)!.AsObject(), clock);
        }
    }

    private static HttpResponseMessage ChatReply(JsonNode request, JsonObject message, TimeProvider clock)
    {
        var streamed = request["stream"]?.GetValue<bool>() == true;
        var choice = new JsonObject { ["index"] = 0, [streamed ? "delta" : "message"] = message,
            ["finish_reason"] = message.ContainsKey("tool_calls") ? "tool_calls" : "stop" };
        var reply = new JsonObject { ["id"] = "chatcmpl-fixture", ["model"] = "pinned", ["created"] = clock.GetUtcNow().ToUnixTimeSeconds(),
            ["object"] = streamed ? "chat.completion.chunk" : "chat.completion", ["choices"] = new JsonArray(choice),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 2 } }.ToJsonString();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streamed ? "data: " + reply + "\n\ndata: [DONE]\n\n" : reply,
            Encoding.UTF8, streamed ? "text/event-stream" : "application/json") };
    }
}
