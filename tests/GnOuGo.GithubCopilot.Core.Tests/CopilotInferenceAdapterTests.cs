using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotInferenceAdapterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyFullyConsumedProviderBodiesSettleTheirOwnReservation(bool finish)
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes("""{"usage":{"input_tokens":4,"output_tokens":3}}""");
        using var owner = new ByteArrayContent(bytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receipts = new List<string>();
        using var stream = new CopilotInferenceStream(new MemoryStream(bytes), owner, deadline, null, async (content, token) =>
            receipts.Add(await content.ReadAsStringAsync(token)));
        var buffer = new byte[7];
        Assert.Equal(7, await stream.ReadAsync(buffer, ct));
        if (finish)
        {
            await stream.CopyToAsync(Stream.Null, ct);
            Assert.Equal(Encoding.UTF8.GetString(bytes), Assert.Single(receipts));
        }
        else Assert.Empty(receipts);
    }

    [Fact]
    public async Task ReservationUsesTheUtf8JsonSentToTheProvider()
    {
        const string content = "Check l’été, the patch <old> & <new>, and the quoted line \"résultat\".";
        var original = new JsonObject { ["input"] = content, ["max_tokens"] = 64 };
        var normalized = new JsonObject { ["input"] = content, ["max_output_tokens"] = 64 };
        var json = normalized.ToJsonString(new System.Text.Json.JsonSerializerOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var ceiling = 4096 + Encoding.UTF8.GetByteCount(json) + 64;
        long persisted = 0;
        var budget = new CopilotInferenceBudget(1, ceiling, DateTimeOffset.UtcNow.AddMinutes(1),
            (_, tokens, _) => { persisted = tokens; return Task.CompletedTask; });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/responses")
            { Content = new StringContent(original.ToJsonString()) };
        await budget.ReserveAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(json, await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ceiling, persisted);
        Assert.Equal(ceiling, budget.ChargedTokens);
    }

    [Theory]
    [InlineData(false, null, true, false, 2, 50_000, "none")]
    [InlineData(true, "auto", true, false, 3, 10_000, "none")]
    [InlineData(false, "auto", true, false, 3, 50_000, "auto")]
    [InlineData(true, "auto", false, false, 2, 50_000, "auto")]
    [InlineData(false, "required", true, false, 2, 50_000, "required")]
    [InlineData(false, "auto", true, true, 2, 50_000, "auto")]
    public async Task LastAffordableNativeTurnPreservesObservedResultsAndOptionalToolPolicy(
        bool responses, string? choice, bool observed, bool retry, int calls, long tokens, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var history = responses ? new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "Inspect the file." })
            : new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Inspect the file." });
        if (observed)
            history.Add(responses
                ? new JsonObject { ["type"] = "function_call_output", ["call_id"] = "read-1", ["output"] = "observed file" }
                : new JsonObject { ["role"] = "tool", ["tool_call_id"] = "read-1", ["content"] = "observed file" });
        var body = new JsonObject
        {
            [responses ? "input" : "messages"] = history,
            ["tools"] = JsonNode.Parse(responses
                ? """[{"type":"function","name":"project_read"}]"""
                : """[{"type":"function","function":{"name":"project_read"}}]"""),
            [responses ? "max_output_tokens" : "max_completion_tokens"] = 512
        };
        if (choice is not null) body["tool_choice"] = choice;
        var budget = new CopilotInferenceBudget(calls, tokens, DateTimeOffset.UtcNow.AddMinutes(1),
            (_, _, _) => Task.CompletedTask, initialCalls: 1, initialTokens: 1000);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            responses ? "https://provider.example/v1/responses" : "https://provider.example/v1/chat/completions")
            { Content = new StringContent(body.ToJsonString()) };
        if (retry) request.Options.Set(CopilotInferenceProxyHandler.RepeatedReservation, true);

        await budget.ReserveAsync(request, ct);

        var wire = await request.Content.ReadAsStringAsync(ct);
        body["tool_choice"] = expected;
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(wire)));
        Assert.Equal(1000 + 4096L + Encoding.UTF8.GetByteCount(wire) + 512, budget.ChargedTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualAdaptedPayloadIsReservedBeforeTheExistingPolicyProxy(bool tooLarge)
    {
        var events = new List<string>(); var sends = 0;
        var body = new JsonObject { ["model"] = "configured", ["max_completion_tokens"] = 64,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new string('x', 3000) }) }.ToJsonString();
        var budget = new CopilotInferenceBudget(2, tooLarge ? 7000 : 20_000, DateTimeOffset.UtcNow.AddMinutes(1),
            (_, _, _) => { events.Add("reserve"); return Task.CompletedTask; });
        using var http = new HttpClient(new Handler(async request =>
        {
            sends++; events.Add("send");
            Assert.Equal("http://127.0.0.1:8123/inference", request.RequestUri!.AbsoluteUri);
            Assert.Equal(body, await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("observed response") };
        }));
        var proxy = new CopilotInferenceProxyHandler(http, new Uri("http://127.0.0.1:8123/inference"));
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
            { Content = new StringContent("{\"max_completion_tokens\":1}") };
        Task<HttpResponseMessage> Send() => CopilotInferenceDispatch.SendAsync(original, Context(), new Adapter(body),
            proxy, budget.ReserveAsync, budget.RecordTransportFailure);
        if (tooLarge)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Send);
            Assert.Equal(0, sends); Assert.Equal(0, budget.ModelCalls); Assert.Empty(events);
        }
        else
        {
            using var response = await Send();
            Assert.Equal("observed response", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(["reserve", "send"], events);
            Assert.Equal(1, sends); Assert.Equal(1, budget.ModelCalls);
            Assert.Equal(Encoding.UTF8.GetByteCount(body) + 4096L + 64, budget.ChargedTokens);
        }
        Assert.Equal("{\"max_completion_tokens\":1}", await original.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReceivedButInvalidDecisionDoesNotBecomeATransportRetryOrReleaseTheReservation()
    {
        var sends = 0;
        var budget = new CopilotInferenceBudget(1, 20_000, DateTimeOffset.UtcNow.AddMinutes(1), (_, _, _) => Task.CompletedTask);
        using var http = new HttpClient(new Handler(_ =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("received malformed decision") });
        }));
        var proxy = new CopilotInferenceProxyHandler(http, new Uri("http://127.0.0.1:8123/inference"));
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
            { Content = new StringContent("{\"max_completion_tokens\":64}") };
        await Assert.ThrowsAsync<InvalidDataException>(() => CopilotInferenceDispatch.SendAsync(original, Context(),
            new Adapter("{\"max_completion_tokens\":64}", invalidResponse: true), proxy, budget.ReserveAsync, budget.RecordTransportFailure));
        Assert.Equal(1, sends); Assert.Equal(1, budget.ModelCalls); Assert.True(budget.ChargedTokens > 4096);
        Assert.False(budget.TransportFailed);
    }

    [Fact]
    public async Task PhysicalDeadlineIncludesTheFirstUnfinishedStreamingRead()
    {
        var failed = false;
        var sends = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
        }));
        var proxy = new CopilotInferenceProxyHandler(http, new Uri("http://127.0.0.1:8123/inference"));
        using var original = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions");
        using var reply = await CopilotInferenceDispatch.SendAsync(original, Context(), null, proxy, null, () => failed = true,
            TimeSpan.FromMilliseconds(100));
        using var stream = await reply.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[1], TestContext.Current.CancellationToken));
        Assert.True(failed);
        Assert.Equal(1, sends);
    }

    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
    }

    [Fact]
    public void CapturedAdapterCannotSilentlyUseAnUnregisteredNativeTransport()
    {
        var config = new CopilotRuntimeConfiguration(Path.GetTempPath(), "configured") { InferenceTransport = "explicit-test-format" };
        Assert.Throws<InvalidOperationException>(() => new GitHubCopilotSdkClientFactory().Create(config));
        var factory = new GitHubCopilotSdkClientFactory(null, null, _ => new Adapter("{}"));
        Assert.Throws<InvalidOperationException>(() => factory.Create(config with { InferenceTransport = null }));
    }

    private sealed class Adapter(string body, bool invalidResponse = false) : CopilotInferenceAdapter
    {
        public override Task<CopilotPreparedInference> PrepareAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
            => Task.FromResult<CopilotPreparedInference>(new Prepared(new HttpRequestMessage(request.Method, request.RequestUri)
                { Content = new StringContent(body) }, invalidResponse));
    }
    private sealed class Prepared(HttpRequestMessage request, bool invalidResponse) : CopilotPreparedInference
    {
        public override HttpRequestMessage Request => request;
        public override Task<HttpResponseMessage> RestoreResponseAsync(HttpResponseMessage response, CancellationToken ct)
            => invalidResponse ? Task.FromException<HttpResponseMessage>(new InvalidDataException("Invalid controlled decision.")) : Task.FromResult(response);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    private static GitHub.Copilot.CopilotRequestContext Context() =>
        (GitHub.Copilot.CopilotRequestContext)Activator.CreateInstance(typeof(GitHub.Copilot.CopilotRequestContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            ["request-id", "https://provider.example/v1/chat/completions", new Dictionary<string, IReadOnlyList<string>>()], null)!;
}
