using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core.Tests;

public sealed class NativeStreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("openai")]
    [InlineData("copilot")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    public async Task FragmentedNativeStructuredResponseHasDurableTerminalAndFinalUsage(string type)
    {
        var journal = new Journal();
        JsonObject? sent = null;
        using var http = NewHttp(new Handler(async request =>
        {
            sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(Ct))!.AsObject();
            return Response(type, Fixture(type));
        }));
        var client = Client(http, type);
        using var context = Context(journal);
        var response = await client.CallAsync(Request(structured: true), Ct);
        Assert.True(sent!["stream"]!.GetValue<bool>());
        Assert.Equal("café", response.Json!["value"]!.GetValue<string>());
        Assert.NotNull(response.Usage);
        var attempt = Assert.Single(journal.State!.Attempts);
        Assert.True(attempt.ProtocolComplete);
        Assert.True(attempt.ResponseBodyComplete);
        Assert.True(attempt.UsageKnown);
        Assert.NotNull(attempt.AssembledBody);
        Assert.Null(attempt.PartialBodyBase64);
        Assert.NotNull(attempt.FirstByteAtUtc);
        Assert.Contains("café", attempt.AssembledBody.Replace("\\u00E9", "é"));
        Assert.True(journal.Saves >= 3);
        Assert.Equal(1, httpCalls(http));
    }

    [Theory]
    [InlineData("openai", "truncated")]
    [InlineData("copilot", "truncated")]
    [InlineData("anthropic", "truncated")]
    [InlineData("ollama", "truncated")]
    [InlineData("openai", "error")]
    [InlineData("copilot", "error")]
    [InlineData("anthropic", "error")]
    [InlineData("ollama", "error")]
    [InlineData("openai", "malformed")]
    [InlineData("anthropic", "malformed")]
    [InlineData("ollama", "malformed")]
    public async Task PartialStreamNeverReturnsAResponseOrRetriesEvenAcrossRecovery(string type, string failure)
    {
        var journal = new Journal();
        using var http = NewHttp(new Handler(_ => Task.FromResult(Response(type, Fixture(type, failure)))));
        var client = Client(http, type);
        using (Context(journal)) await Assert.ThrowsAnyAsync<Exception>(() => client.CallAsync(Request(), Ct));
        var attempt = Assert.Single(journal.State!.Attempts);
        Assert.Null(attempt.Status);
        Assert.NotEqual(true, attempt.ProtocolComplete);
        Assert.NotNull(attempt.PartialBodyBase64);
        using (Context(journal)) await Assert.ThrowsAnyAsync<Exception>(() => Client(http, type).CallAsync(Request(), Ct));
        Assert.Equal(1, httpCalls(http));
        Assert.Single(journal.State!.Attempts);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("copilot")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    public async Task MissingUsageRemainsUnknown(string type)
    {
        var journal = new Journal();
        using var http = NewHttp(new Handler(_ => Task.FromResult(Response(type, Fixture(type, "no-usage")))));
        using var context = Context(journal);
        var response = await Client(http, type).CallAsync(Request(), Ct);
        Assert.Null(response.Usage);
        Assert.False(journal.State!.Attempts.Single().UsageKnown);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("copilot")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    public async Task CancellationAfterPartialBytesIsRetainedAndNotRetried(string type)
    {
        var journal = new Journal();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var bytes = Encoding.UTF8.GetBytes(Fixture(type, "truncated"));
        using var http = NewHttp(new Handler(_ => Task.FromResult(Response(type, new FragmentStream(bytes, waitAtEnd: true)))));
        using var context = Context(journal);
        var task = Client(http, type).CallAsync(Request(), cancel.Token);
        await journal.FirstPartial.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal("cancelled", journal.State!.Attempts.Single().Failure);
        Assert.NotNull(journal.State.Attempts.Single().PartialBodyBase64);
        Assert.Equal(1, httpCalls(http));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalPublicationFailureRecoversOnlyDurablyCompletedEvidence(bool persisted)
    {
        var journal = new Journal { FailTerminal = true, PersistBeforeFailure = persisted };
        using var http = NewHttp(new Handler(_ => Task.FromResult(Response("openai", Fixture("openai")))));
        using (Context(journal)) await Assert.ThrowsAsync<IOException>(() => Client(http, "openai").CallAsync(Request(), Ct));
        journal.FailTerminal = false;
        using (Context(journal))
        {
            if (persisted) Assert.Equal("{\"value\":\"café\"}", (await Client(http, "openai").CallAsync(Request(), Ct)).Text);
            else await Assert.ThrowsAnyAsync<Exception>(() => Client(http, "openai").CallAsync(Request(), Ct));
        }
        Assert.Equal(1, httpCalls(http));
        Assert.Single(journal.State!.Attempts);
    }

    [Fact]
    public async Task ConfirmedTransientErrorUsesOneRetryOwnerAndFreshStreamAssembly()
    {
        var journal = new Journal();
        var calls = 0;
        using var http = NewHttp(new Handler(_ => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":{\"type\":\"server_error\"}}") }
            : Response("openai", Fixture("openai")))));
        using var context = Context(journal);
        var response = await Client(http, "openai").CallAsync(Request(), Ct);
        Assert.Equal("{\"value\":\"café\"}", response.Text);
        Assert.Equal(2, calls);
        Assert.Equal(2, journal.State!.Attempts.Count);
        Assert.Equal(2, journal.State.Attempts.Select(a => a.Id).Distinct().Count());
        Assert.Null(journal.State.Attempts[0].AssembledBody);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("copilot")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    public async Task UnknownCustomRouteRejectsExplicitStreamingBeforeSend(string type)
    {
        using var http = NewHttp(new Handler(_ => throw new InvalidOperationException("Must not dispatch")));
        var provider = Provider(type);
        provider.RequestPolicy.StreamingSupport = null;
        var client = new RoutingLLMClient(http, Options(provider));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync(Request(), Ct));
        Assert.Equal(0, httpCalls(http));
    }

    [Fact]
    public void AutoAndExactDeploymentOverridesResolveBeforeDispatch()
    {
        var provider = Provider("openai");
        provider.RequestPolicy.StreamingMode = LLMStreamingMode.Auto;
        Assert.True(LLMTransportResolver.Resolve("deployment", provider, false).IsStreaming);
        provider.ModelTransportOverrides = new() { ["deployment"] = new() { StreamingMode = LLMStreamingMode.NonStreaming } };
        Assert.False(LLMTransportResolver.Resolve("deployment", provider, false).IsStreaming);
        Assert.True(LLMTransportResolver.Resolve("other", provider, false).IsStreaming);
        provider.RequestPolicy.StreamingSupport = null;
        Assert.False(LLMTransportResolver.Resolve("other", provider, false).IsStreaming);
        Assert.Throws<InvalidOperationException>(() => LLMTransportResolver.Resolve("other", new() { Type = "anthropic", RequestPolicy = new() { StreamingMode = LLMStreamingMode.Streaming } }, true));
    }

    [Fact]
    public async Task NativeToolArgumentsAreAssembledBeforeExistingParserReceivesThem()
    {
        var stream = Event(new { id = "r", choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, id = "call", type = "function", function = new { name = "lookup", arguments = "{\"id\":" } } } } } } })
            + Event(new { id = "r", choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, function = new { arguments = "7}" } } } }, finish_reason = "tool_calls" } } }) + "data: [DONE]\n\n";
        using var http = NewHttp(new Handler(_ => Task.FromResult(Response("openai", stream))));
        var response = await Client(http, "openai").CallAsync(Request(), Ct);
        var call = Assert.Single(response.ToolCalls!);
        Assert.Equal("lookup", call.Name);
        Assert.Equal(7, call.Arguments!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task RefusalAndOutputLimitArePreservedAsTerminalOutcomes()
    {
        foreach (var finish in new[] { "length", "content_filter" })
        {
            var stream = Event(new { id = "r", choices = new[] { new { index = 0, delta = new { refusal = "Cannot comply" }, finish_reason = finish } } }) + "data: [DONE]\n\n";
            using var http = NewHttp(new Handler(_ => Task.FromResult(Response("openai", stream))));
            var response = await Client(http, "openai").CallAsync(Request(), Ct);
            Assert.Equal(finish, response.Raw!["choices"]![0]!["finish_reason"]!.GetValue<string>());
            Assert.Equal("Cannot comply", response.Raw["choices"]![0]!["message"]!["refusal"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ResponsesTerminalObjectPreservesOutputAndUsageWithoutChangingBackgroundRoute()
    {
        var body = Event(new { type = "response.created", sequence_number = 0, response = new { id = "resp_1", status = "in_progress" } })
            + Event(new { type = "response.output_text.delta", sequence_number = 1, delta = "ok" })
            + Event(new { type = "response.completed", sequence_number = 2, response = new { id = "resp_1", status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "ok" } } } }, usage = new { input_tokens = 3, output_tokens = 1 } } });
        using var http = NewHttp(new Handler(async req =>
        {
            Assert.EndsWith("/responses", req.RequestUri!.AbsolutePath);
            var sent = JsonNode.Parse(await req.Content!.ReadAsStringAsync(Ct))!;
            Assert.True(sent["background"]!.GetValue<bool>());
            Assert.True(sent["stream"]!.GetValue<bool>());
            return Response("openai", body);
        }));
        var request = Request(); request.UseBackgroundMode = true;
        var response = await Client(http, "openai").CallAsync(request, Ct);
        Assert.Equal("ok", response.Text);
        Assert.NotNull(response.Usage);
    }

    [Fact]
    public async Task ActualStreamFlagAvoidsControlledGatewayBufferedLongCallFailure()
    {
        var bytes = Encoding.UTF8.GetBytes(Fixture("openai"));
        var prefix = Encoding.UTF8.GetByteCount(Fixture("openai")[..(Fixture("openai").IndexOf("\n\n", StringComparison.Ordinal) + 2)]);
        using var http = NewHttp(new Handler(async req =>
        {
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(Ct))!;
            if (body["stream"]?.GetValue<bool>() == true)
                return Response("openai", new FragmentStream(bytes, delayAt: prefix, pause: TimeSpan.FromMilliseconds(250)));
            await Task.Delay(100, Ct); // Scaled gateway cutoff while buffering a non-streaming generation.
            return new HttpResponseMessage(HttpStatusCode.GatewayTimeout) { Content = new StringContent("buffered generation cutoff") };
        }));
        var journal = new Journal();
        using (Context(journal)) Assert.NotEmpty((await Client(http, "openai").CallAsync(Request(), Ct)).Text);
        var attempt = journal.State!.Attempts.Single();
        Assert.True(attempt.LastByteAtUtc - attempt.FirstByteAtUtc >= TimeSpan.FromMilliseconds(200));
        Assert.True(attempt.ProtocolComplete);
        var provider = Provider("openai"); provider.RequestPolicy.StreamingMode = LLMStreamingMode.NonStreaming;
        await Assert.ThrowsAsync<LLMProviderException>(() => new RoutingLLMClient(http, Options(provider)).CallAsync(Request(), Ct));
        Assert.Equal(2, httpCalls(http));
    }

    [Fact]
    public async Task BackgroundStreamRecoveryRetrievesTheSameResponseWithoutAnotherPostOrCharge()
    {
        var posts = 0; var gets = 0; var journal = new Journal();
        using var http = NewHttp(new Handler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                posts++;
                return Task.FromResult(Response("openai", Event(new { type = "response.created", sequence_number = 0, response = new { id = "resp_kept", status = "in_progress" } })));
            }
            gets++;
            Assert.EndsWith("/responses/resp_kept", req.RequestUri!.AbsolutePath);
            Assert.Equal("test", req.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"resp_kept\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"retrieved\"}]}],\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}", Encoding.UTF8, "application/json") });
        }));
        var request = Request(); request.UseBackgroundMode = true;
        using (Context(journal)) Assert.Equal("retrieved", (await Client(http, "openai").CallAsync(request, Ct)).Text);
        var retained = JsonSerializer.Serialize(journal.State, LLMHttpRetryJsonContext.Default.LLMHttpRetryState);
        using (Context(journal)) Assert.Equal("retrieved", (await Client(http, "openai").CallAsync(request, Ct)).Text);
        Assert.Equal(retained, JsonSerializer.Serialize(journal.State, LLMHttpRetryJsonContext.Default.LLMHttpRetryState));
        Assert.Equal(1, posts); Assert.Equal(1, gets);
        var attempt = Assert.Single(journal.State!.Attempts);
        Assert.Single(attempt.ReceiptRetrievals!);
        Assert.NotNull(attempt.PartialBodyBase64);
        Assert.Equal("transport", attempt.StreamFailure);
        Assert.True(attempt.ProtocolComplete);
    }

    [Fact]
    public async Task InitialAnthropicZeroIsNotFinalUsageWhenFinalUsageIsMissing()
    {
        var stream = Fixture("anthropic").Replace("\"usage\":{\"output_tokens\":5}", "\"usage\":null", StringComparison.Ordinal);
        var journal = new Journal();
        using var http = NewHttp(new Handler(_ => Task.FromResult(Response("anthropic", stream))));
        using var context = Context(journal);
        var response = await Client(http, "anthropic").CallAsync(Request(), Ct);
        Assert.Null(response.Usage?["output_tokens"]);
        Assert.False(journal.State!.Attempts.Single().UsageKnown);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    public async Task NativeToolsRemainCompleteArgumentsWithoutDispatchingThem(string type)
    {
        string body;
        if (type == "anthropic")
            body = Event(new { type = "message_start", message = new { id = "r", role = "assistant" } })
                + Event(new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = "call", name = "lookup", input = new { } } })
                + Event(new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "{\"id\":" } })
                + Event(new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = "7}" } })
                + Event(new { type = "content_block_stop", index = 0 })
                + Event(new { type = "message_delta", delta = new { stop_reason = "tool_use" } }) + Event(new { type = "message_stop" });
        else body = "{\"message\":{\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"lookup\",\"arguments\":{\"id\":7}}}]},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n";
        using var http = NewHttp(new Handler(_ => Task.FromResult(Response(type, body))));
        var response = await Client(http, type).CallAsync(Request(), Ct);
        Assert.Equal(7, Assert.Single(response.ToolCalls!).Arguments!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task PartialLocalInferenceCannotTriggerRetryOrCloudFallback()
    {
        var runtime = new PartialLocalRuntime();
        using var http = NewHttp(new Handler(_ => throw new InvalidOperationException("Cloud fallback must not dispatch")));
        var options = new LLMOptions { DefaultProvider = "embedded", DefaultModel = "deployment", Fallback = new() { Provider = "cloud", Model = "deployment" },
            Models = new() { ["embedded"] = new() { Type = "local", RequestPolicy = new() { StreamingMode = LLMStreamingMode.Streaming,
                StreamingSupport = new() { [LLMTransportResolver.Local] = true } } }, ["cloud"] = Provider("openai") } };
        var client = new RoutingLLMClient(options, [new LocalLLMProvider(runtime), new OpenAiLLMProvider(http)]);
        await Assert.ThrowsAnyAsync<Exception>(() => client.CallAsync(Request(), Ct));
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(0, httpCalls(http));
    }
    private sealed class PartialLocalRuntime : ILocalLLMRuntime, ILocalLLMStreamingRuntime
    {
        internal int Calls;
        public bool SupportsNativeStreaming => true;
        public Task<LLMClientResponse> CallAsync(LLMClientRequest request, CancellationToken ct = default)
        {
            Calls++;
            throw new LocalLLMException(LocalLLMFailureKind.Inference, "native inference interrupted", generationStarted: true);
        }
    }

    private static LLMClientRequest Request(bool structured = false) => new() { Model = "deployment", Prompt = "bounded", MaxOutputTokens = 1024,
        StructuredOutputSchema = structured ? JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"],\"additionalProperties\":false}") : null };
    private static LLMOptions Options(ModelProviderOptions provider) => new() { DefaultProvider = "test", DefaultModel = "deployment", Models = new() { ["test"] = provider } };
    private static ModelProviderOptions Provider(string type) => new()
    {
        Type = type, Url = "https://gateway.example/v1", ApiKey = "test",
        RequestPolicy = new() { StreamingMode = LLMStreamingMode.Streaming, StreamingSupport = new() {
            [LLMTransportResolver.ChatCompletions] = true, [LLMTransportResolver.Messages] = true, [LLMTransportResolver.OllamaChat] = true, [LLMTransportResolver.Responses] = true } },
        RetryPolicy = new() { BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 }
    };
    private static RoutingLLMClient Client(HttpClient http, string type) => new(http, Options(Provider(type)));
    private static IDisposable Context(Journal journal) => new LLMHttpRetryContext("request", journal, new() { MaxAttempts = 3, MaxUncertainRetries = 0, BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 }).Activate();
    private static HttpResponseMessage Response(string type, string text) => Response(type, new FragmentStream(Encoding.UTF8.GetBytes(text)));
    private static HttpResponseMessage Response(string type, Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type == "ollama" ? "application/x-ndjson" : "text/event-stream");
        return response;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, Handler> Handlers = new();
    private static HttpClient NewHttp(Handler handler) { var http = new HttpClient(handler); Handlers.Add(http, handler); return http; }
    private static int httpCalls(HttpClient http) => Handlers.GetValue(http, _ => throw new InvalidOperationException()).Calls;
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request); }
    }
    private static string Event<T>(T value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
    private static string Fixture(string type, string? failure = null)
    {
        var usage = failure != "no-usage";
        string start, end;
        if (type == "anthropic")
        {
            start = Event(new { type = "message_start", message = new { id = "r", type = "message", role = "assistant", usage = usage ? new { input_tokens = 10, output_tokens = 0 } : null } })
                + Event(new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
                + Event(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "{\"value\":\"café\"}" } });
            end = Event(new { type = "content_block_stop", index = 0 })
                + Event(new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = usage ? new { output_tokens = 5 } : null }) + Event(new { type = "message_stop" });
        }
        else if (type == "ollama")
        {
            start = "{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"value\\\":\\\"café\\\"}\"},\"done\":false}\n";
            end = usage ? "{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":10,\"eval_count\":5}\n" : "{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n";
        }
        else
        {
            start = Event(new { id = "r", choices = new[] { new { index = 0, delta = new { content = "{\"value\":\"ca" } } } })
                + Event(new { id = "r", choices = new[] { new { index = 0, delta = new { content = "fé\"}" } } } });
            end = Event(new { id = "r", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } } });
            if (usage) end += Event(new { id = "r", choices = Array.Empty<object>(), usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 } });
            end += "data: [DONE]\n\n";
        }
        return failure switch
        {
            "truncated" => start,
            "error" => start + (type == "ollama" ? "{\"error\":\"failed\"}\n" : Event(new { type = "error", error = new { type = "overloaded_error" } })),
            "malformed" => start + (type == "ollama" ? "{broken\n" : "data: {broken\n\n") + end,
            _ => start + end
        };
    }
    private sealed class Journal : ILLMHttpRetryJournal
    {
        internal LLMHttpRetryState? State;
        internal int Saves;
        internal bool FailTerminal, PersistBeforeFailure;
        internal TaskCompletionSource FirstPartial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct) => Task.FromResult(State is null ? null : Clone(State));
        public Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
        {
            Saves++;
            var fail = FailTerminal && state.Attempts.LastOrDefault()?.ProtocolComplete == true;
            if (!fail || PersistBeforeFailure) State = Clone(state);
            if (state.Attempts.LastOrDefault()?.PartialBodyBase64 is not null) FirstPartial.TrySetResult();
            if (fail) throw new IOException("durable publication interrupted");
            return Task.CompletedTask;
        }
        private static LLMHttpRetryState Clone(LLMHttpRetryState state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState), LLMHttpRetryJsonContext.Default.LLMHttpRetryState)!;
    }
    private sealed class FragmentStream(byte[] bytes, bool waitAtEnd = false, int? delayAt = null, TimeSpan? pause = null) : Stream
    {
        private int _offset;
        private bool _paused;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_paused && delayAt is { } boundary && _offset >= boundary)
            { _paused = true; await Task.Delay(pause ?? TimeSpan.Zero, cancellationToken); }
            if (_offset == bytes.Length && waitAtEnd) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var count = Math.Min(Math.Min(3, buffer.Length), bytes.Length - _offset);
            if (!_paused && delayAt is { } next && _offset < next) count = Math.Min(count, next - _offset);
            bytes.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
