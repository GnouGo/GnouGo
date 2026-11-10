using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core.Tests;

/// <summary>Real loopback HTTP/1.1, not provider inference or a campaign replay.</summary>
public sealed class NativeStreamingSocketTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("complete")]
    [InlineData("http_eof")]
    [InlineData("socket_reset")]
    [InlineData("missing_done")]
    [InlineData("deadline")]
    public async Task NativeSocketSeparatesSilentGapProtocolTerminationAndUsage(string ending)
    {
        var ct = TestContext.Current.CancellationToken;
        var configured = Environment.GetEnvironmentVariable("GNOU_GO_NATIVE_STREAM_IDLE_SECONDS");
        var silence = TimeSpan.FromMilliseconds(100);
        if (ending == "complete" && configured is not null)
        {
            if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 450)
                throw new InvalidOperationException("The controlled idle interval must be 1–450 seconds.");
            silence = TimeSpan.FromSeconds(seconds);
        }
        if (ending == "deadline") silence = TimeSpan.FromSeconds(10);
        await using var fixture = new SocketFixture(ending, silence, ct);
        var directory = Path.Combine(Path.GetTempPath(), "gnougo-native-socket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journal = new FileJournal(Path.Combine(directory, "attempt.json"));
            var policy = new LLMProviderRetryPolicyOptions { MaxAttempts = 3, MaxUncertainRetries = 0,
                AttemptTimeoutMilliseconds = ending == "deadline" ? 1_000 : 600_000 };
            using var http = LLMHttpClientFactory.Create();
            var options = new LLMOptions { DefaultProvider = "fixture", DefaultModel = "fixture-model",
                ModelOverrides = { ["openai/fixture-model"] = new() { MaxOutputTokens = 32768, Capabilities = new()
                {
                    SupportsStructuredOutput = true, SupportsReasoningEffort = true,
                    SupportsChatCompletions = true, SupportedReasoningEfforts = ["low"]
                } } }, Models =
            {
                ["fixture"] = new() { Type = "openai", Url = fixture.Url,
                    ModelTransportOverrides = new() { ["fixture-model"] = new() { StreamingSupport = new() { [LLMTransportResolver.ChatCompletions] = true } } },
                    RequestPolicy = new()
                {
                    StreamingMode = LLMStreamingMode.Streaming,
                    StreamingSupport = new() { [LLMTransportResolver.ChatCompletions] = true }
                } }
            } };
            var request = new LLMClientRequest { Model = "fixture-model", Prompt = "Return the requested JSON value.",
                MaxOutputTokens = 32768, Reasoning = "low", StructuredOutputStrict = true,
                StructuredOutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"],"additionalProperties":false}""") };
            var timer = Stopwatch.StartNew();
            using (new LLMHttpRetryContext("controlled-socket", journal, policy).Activate())
            {
                if (ending == "complete")
                    Assert.Equal(42, (await new RoutingLLMClient(http, options).CallAsync(request, ct)).Json!["value"]!.GetValue<int>());
                else
                {
                    var error = await Assert.ThrowsAsync<LLMProviderException>(() => new RoutingLLMClient(http, options).CallAsync(request, ct));
                    Assert.Equal(ending == "deadline" ? LLMProviderFailureKind.Timeout : LLMProviderFailureKind.Transport, error.Kind);
                }
            }
            var state = (await new FileJournal(journal.Path).LoadAsync(ct))!;
            var attempt = Assert.Single(state.Attempts);
            Assert.Equal(200, attempt.ObservedStatus);
            Assert.Equal("fixture-response", attempt.ProviderResponseId);
            Assert.NotNull(attempt.FirstByteAtUtc);
            Assert.Equal(ending == "complete", attempt.ProtocolComplete == true);
            Assert.Equal(ending is "complete" or "missing_done", attempt.UsageKnown == true);
            Assert.Equal(ending == "complete", attempt.ResponseBodyComplete == true);
            Assert.Equal(TimeSpan.FromMilliseconds(policy.AttemptTimeoutMilliseconds), attempt.DeadlineAtUtc - attempt.StartedAtUtc);
            Assert.Equal(1, fixture.Requests);
            if (ending == "complete")
            {
                Assert.True(timer.Elapsed >= silence);
                Assert.True(attempt.LastByteAtUtc - attempt.FirstByteAtUtc >= silence - TimeSpan.FromSeconds(1));
                Assert.Null(attempt.Failure);
            }
            else
            {
                Assert.Null(attempt.Body);
                Assert.Null(attempt.AssembledBody);
                Assert.NotNull(attempt.PartialBodyBase64);
                // A cold reader may inspect this synthetic failure but cannot send it again.
                var retained = await File.ReadAllTextAsync(journal.Path, ct);
                using var cold = LLMHttpClientFactory.Create();
                using (new LLMHttpRetryContext("controlled-socket", new FileJournal(journal.Path), policy).Activate())
                    await Assert.ThrowsAnyAsync<Exception>(() => new RoutingLLMClient(cold, options).CallAsync(request, ct));
                Assert.Equal(retained, await File.ReadAllTextAsync(journal.Path, ct));
                Assert.Equal(1, fixture.Requests);
            }
            output.WriteLine(JsonSerializer.Serialize(new { controlled_fixture = true, provider_calls = 0, ending,
                silence_seconds = silence.TotalSeconds, elapsed_seconds = timer.Elapsed.TotalSeconds,
                http_version = fixture.HttpVersion, request_stream = fixture.RequestBody?["stream"],
                request_max_output = fixture.RequestBody?["max_completion_tokens"], attempt },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FileJournal(string path) : ILLMHttpRetryJournal
    {
        internal string Path { get; } = path;
        public async Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct) => File.Exists(Path)
            ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path, ct), LLMHttpRetryJsonContext.Default.LLMHttpRetryState) : null;
        public async Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Path + ".next", JsonSerializer.Serialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState), ct);
            File.Move(Path + ".next", Path, overwrite: true);
        }
    }

    private sealed class SocketFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop;
        private readonly Task _server;
        internal int Requests { get; private set; }
        internal string? HttpVersion { get; private set; }
        internal JsonObject? RequestBody { get; private set; }
        internal string Url { get; }
        internal SocketFixture(string ending, TimeSpan silence, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener.Start(); // Keep the port-zero socket owned until disposal.
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/v1/chat/completions";
            _server = ServeAsync(ending, silence, _stop.Token);
        }
        private async Task ServeAsync(string ending, TimeSpan silence, CancellationToken ct)
        {
            using var connection = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = connection.GetStream();
            var headers = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                await stream.ReadExactlyAsync(one, ct); headers.Add(one[0]);
                if (headers.Count > 16_384) throw new InvalidDataException("Controlled HTTP header exceeds its bound.");
                if (headers.Count >= 4 && headers.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
            }
            var lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n");
            HttpVersion = lines[0].Split(' ')[2];
            var length = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], CultureInfo.InvariantCulture);
            if (length is < 1 or > 1_048_576) throw new InvalidDataException("Controlled request size is outside its bound.");
            var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
            RequestBody = JsonNode.Parse(bytes)!.AsObject(); Requests++;
            Assert.Equal("HTTP/1.1", HttpVersion);
            Assert.True(RequestBody["stream"]!.GetValue<bool>());
            Assert.Equal("json_schema", RequestBody["response_format"]!["type"]!.ToString());
            Assert.True(RequestBody["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
            Assert.Equal("low", RequestBody["reasoning_effort"]!.ToString());
            Assert.Equal(32768, RequestBody["max_completion_tokens"]!.GetValue<int>());
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
            await ChunkAsync(stream, Event(new { choices = Array.Empty<object>(), prompt_filter_results = Array.Empty<object>() })
                + Event(new { id = "fixture-response", choices = new[] { new { index = 0, delta = new { role = "assistant", content = "" } } } }), ct);
            await Task.Delay(silence, ct);
            if (ending == "socket_reset") { connection.Client.LingerState = new(true, 0); return; }
            if (ending is "complete" or "missing_done")
            {
                var tail = Event(new { id = "fixture-response", choices = new[] { new { index = 0, delta = new { content = "{\"value\":42}" }, finish_reason = "stop" } } })
                    + Event(new { id = "fixture-response", choices = Array.Empty<object>(), usage = new { prompt_tokens = 7, completion_tokens = 3 } });
                if (ending == "complete") tail += "data: [DONE]\n\n";
                await ChunkAsync(stream, tail, ct);
            }
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }
        private static string Event<T>(T value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
        private static async Task ChunkAsync(NetworkStream stream, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(bytes.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), ct);
            await stream.WriteAsync(bytes, ct); await stream.WriteAsync("\r\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync(); _listener.Stop();
            try { await _server; }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { _stop.Dispose(); }
        }
    }
}
