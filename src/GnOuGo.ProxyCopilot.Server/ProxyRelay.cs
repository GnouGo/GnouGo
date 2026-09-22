using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Configuration;
using GnOuGo.ProxyCopilot.Server.Protocols;
using GnOuGo.ProxyCopilot.Server.Traffic;

namespace GnOuGo.ProxyCopilot.Server;

public sealed class ProxyRelay : IDisposable
{
    public const string TelemetryName = "GnOuGo.ProxyCopilot";
    private static readonly ActivitySource Activities = new(TelemetryName);
    private static readonly Meter Meter = new(TelemetryName);
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("gnougo.proxy.calls");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gnougo.proxy.duration", "ms");
    private readonly ProxyOptions _options;
    private readonly IModelRegistry _models;
    private readonly ITrafficStore _traffic;
    private readonly IProxyAuthentication _authentication;
    private readonly HttpClient _http;
    private readonly Dictionary<string, IProxyAdapter> _adapters;
    private readonly SemaphoreSlim _concurrency;
    private readonly ILogger<ProxyRelay> _logger;

    public ProxyRelay(ProxyOptions options, IModelRegistry models, ITrafficStore traffic, IProxyAuthentication authentication,
        HttpClient http, IEnumerable<IProxyAdapter> adapters, ILogger<ProxyRelay> logger)
    {
        _options = options; _models = models; _traffic = traffic; _authentication = authentication; _http = http;
        _adapters = adapters.ToDictionary(a => a.Type, StringComparer.Ordinal); _logger = logger;
        _concurrency = new(options.MaxConcurrentRequests);
    }

    public async Task Handle(HttpContext context)
    {
        if (!await _concurrency.WaitAsync(0, context.RequestAborted)) throw new ProxyException(429, "proxy_busy", "The proxy has reached its concurrent request limit.");
        try { await Relay(context); }
        finally { _concurrency.Release(); }
    }

    private async Task Relay(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new ProxyException(415, "invalid_content_type", "Use application/json.");
        var bytes = await ReadRequest(context.Request.Body, _options.MaxRequestBytes, context.RequestAborted);
        JsonObject request;
        try { request = JsonNode.Parse(bytes)?.AsObject() ?? throw new JsonException(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        { throw new ProxyException(400, "invalid_json", "The request must be a JSON object."); }
        var route = _models.Resolve(ChatContract.Text(request["model"], "model"));
        var callId = _traffic.Start(route, bytes);
        var started = Stopwatch.GetTimestamp();
        var status = "failed";
        using var activity = Activities.StartActivity("llm.proxy", ActivityKind.Client);
        activity?.SetTag("TenantId", _options.TenantId);
        activity?.SetTag("gen_ai.provider.name", route.Type);
        activity?.SetTag("gen_ai.request.model", route.Id);
        activity?.SetTag("gnougo.proxy.request_id", callId);
        context.Response.Headers["X-GnOuGo-Request-Id"] = callId;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var policy = route.Options.Connection.RetryPolicy;
        var ct = timeout.Token;
        var readingUpstream = false;
        try
        {
            var adapter = _adapters[route.Type];
            byte[] upstreamBody;
            try
            {
                var prepared = ChatContract.PrepareRequest(request, route);
                upstreamBody = Encoding.UTF8.GetBytes(adapter.CreateRequest(prepared, route).ToJsonString(ProxyJsonContext.Default.Options));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or FormatException)
            { throw new ProxyException(400, "invalid_request", "The chat request contains invalid fields."); }
            _traffic.Append(callId, "upstreamRequest", upstreamBody);
            var streaming = ChatContract.Bool(request["stream"]);
            var includeUsage = ChatContract.Bool(request["stream_options"]?["include_usage"]);
            HttpResponseMessage? response = null;
            var totalDelay = TimeSpan.Zero;
            try
            {
                for (var attempt = 1; attempt <= policy.MaxAttempts; attempt++)
                {
                    timeout.CancelAfter(policy.AttemptTimeoutMilliseconds);
                    using var outgoing = new HttpRequestMessage(HttpMethod.Post, adapter.Endpoint(route));
                    outgoing.Content = new ByteArrayContent(upstreamBody);
                    outgoing.Content.Headers.ContentType = new("application/json");
                    outgoing.Headers.Accept.Add(new(streaming && route.Type != "ollama" ? "text/event-stream" : "application/json"));
                    await _authentication.Apply(outgoing, route, callId, ct);
                    // No client credentials, cookies, forwarding headers, or arbitrary URLs
                    // are inherited from the incoming request. Redirects are disabled.
                    response = await _http.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.IsSuccessStatusCode) break;
                    await CaptureError(response, callId, ct);
                    var delay = RetryDelay(response, attempt, policy.BaseDelayMilliseconds, policy.MaxDelayMilliseconds);
                    var retry = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                        && attempt < policy.MaxAttempts && totalDelay + delay <= TimeSpan.FromMilliseconds(policy.MaxTotalDelayMilliseconds);
                    if (!retry) throw new ProxyException((int)response.StatusCode, "upstream_rejected", $"The upstream rejected the request (HTTP {(int)response.StatusCode}).");
                    response.Dispose(); response = null;
                    totalDelay += delay;
                    timeout.CancelAfter(Timeout.Infinite);
                    await Task.Delay(delay, context.RequestAborted);
                }
                if (response is null) throw WireReader.Invalid("No upstream response was received.");
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (streaming && route.Type != "ollama" && mediaType != "text/event-stream")
                    throw WireReader.Invalid("The upstream did not return an SSE stream.");
                context.Response.StatusCode = 200;
                context.Response.ContentType = streaming ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var captured = new CaptureReadStream(stream, _traffic, callId);
                readingUpstream = true;
                var toolStream = new ToolCallStream();
                await foreach (var chunk in adapter.ReadResponse(captured, streaming, includeUsage, route, ct))
                {
                    _traffic.Progress(callId, chunk);
                    // Usage remains available to the dashboard even when the client did
                    // not request the optional trailing usage-only streaming chunk.
                    if (streaming && !includeUsage && chunk["choices"] is JsonArray { Count: 0 } && chunk["usage"] is not null) continue;
                    foreach (var output in streaming ? toolStream.Process(chunk) : [chunk])
                    {
                        var json = output.ToJsonString(ProxyJsonContext.Default.Options);
                        await Emit(context, callId, streaming ? "data: " + json + "\n\n" : json, ct);
                    }
                }
                if (streaming) await Emit(context, callId, "data: [DONE]\n\n", ct);
                status = "completed";
                _traffic.Complete(callId, status, 200);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            finally { response?.Dispose(); }
        }
        catch (Exception ex)
        {
            var error = ex switch
            {
                ProxyException known when readingUpstream && known.StatusCode < 500 => WireReader.Invalid("The upstream returned an invalid chat response."),
                ProxyException known => known,
                OperationCanceledException when context.RequestAborted.IsCancellationRequested => new ProxyException(499, "client_cancelled", "The client cancelled the request."),
                OperationCanceledException => new ProxyException(504, "upstream_timeout", "The upstream request timed out."),
                HttpRequestException or IOException => new ProxyException(502, "upstream_transport_error", "The upstream connection failed."),
                _ => new ProxyException(502, "invalid_upstream_response", "The upstream response could not be processed.")
            };
            status = error.StatusCode == 499 ? "cancelled" : "failed";
            _traffic.Complete(callId, status, error.StatusCode, error.Message);
            activity?.SetStatus(ActivityStatusCode.Error, error.Code);
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) context.Abort();
            else await ProxyApplication.WriteError(context, error);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var tags = new TagList { { "TenantId", _options.TenantId }, { "provider", route.Type }, { "status", status } };
            Calls.Add(1, tags); Duration.Record(elapsed, tags);
            _logger.LogInformation("Proxy call {RequestId} finished with {Status} in {DurationMs:F0} ms. TenantId={TenantId}", callId, status, elapsed, _options.TenantId);
        }
    }

    private async Task CaptureError(HttpResponseMessage response, string id, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4096];
        var remaining = Math.Min(_options.Capture.MaxBodyBytes + 1, 64 * 1024);
        while (remaining > 0)
        {
            var count = await body.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), ct);
            if (count == 0) break;
            _traffic.Append(id, "upstreamResponse", buffer.AsSpan(0, count));
            remaining -= count;
        }
    }

    private async Task Emit(HttpContext context, string id, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _traffic.Append(id, "clientResponse", bytes);
        await context.Response.Body.WriteAsync(bytes, ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt, int initial, int maximum)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date) return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Math.Min(maximum, initial * Math.Pow(2, attempt - 1)));
    }

    private static async Task<byte[]> ReadRequest(Stream stream, int maximum, CancellationToken ct)
    {
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + count > maximum) throw new ProxyException(413, "request_too_large", "The request exceeds the configured size limit.");
            body.Write(buffer, 0, count);
        }
        return body.ToArray();
    }

    public void Dispose() => _concurrency.Dispose();
}
