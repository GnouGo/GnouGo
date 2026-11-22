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
        await new CopilotInferenceProxyHandler(local, endpoint).VerifyReadyAsync(Ct);
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
        Assert.Single(upstream.Ids);
    }

    [Fact]
    public async Task LocalSdkReceivesRetryCreditAfterAToolAndRunsItOnlyOnce()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GNOUGO_TEST_LOCAL_COPILOT") == "1", "Opt in to the bundled local Copilot runtime.");
        using var fixture = new Fixture();
        var options = RuntimeOptions();
        options.Models["fixture"].Url = "http://127.0.0.1:1/v1";
        using var handler = new RetryProvider(TimeProvider.System, 429, tool: true);
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
            Environment: new Dictionary<string, string> { ["COPILOT_HOME"] = fixture.Root, ["COPILOT_TELEMETRY"] = "false" })
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
        var answer = await session.SendAsync(session.SessionId, new(context, session.SessionId, prompt, TimeoutSeconds: 60), Ct);
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

    private sealed class RetryProvider(TimeProvider clock, int status, bool tool = false) : HttpMessageHandler
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
                rejected.Headers.TryAddWithoutValidation("retry-after-ms", "10");
                return rejected;
            }
            var selecting = tool && Ids.Count == 1;
            var message = selecting ? """{"role":"assistant","tool_calls":[{"index":0,"id":"call_fixture","type":"function","function":{"name":"fixture_observe","arguments":"{}"}}]}"""
                : """{"role":"assistant","content":"observed"}""";
            var streamed = JsonNode.Parse(Bodies[^1])?["stream"]?.GetValue<bool>() == true;
            var choice = new JsonObject { ["index"] = 0, [streamed ? "delta" : "message"] = JsonNode.Parse(message),
                ["finish_reason"] = selecting ? "tool_calls" : "stop" };
            var reply = new JsonObject { ["id"] = "chatcmpl-fixture", ["model"] = "pinned", ["created"] = clock.GetUtcNow().ToUnixTimeSeconds(),
                ["object"] = streamed ? "chat.completion.chunk" : "chat.completion", ["choices"] = new JsonArray(choice),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 2 } }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streamed ? "data: " + reply + "\n\ndata: [DONE]\n\n" : reply,
                Encoding.UTF8, streamed ? "text/event-stream" : "application/json") };
        }
    }
}
