using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Non-renewable inference reservations, independent of tool permissions and SDK session lifetime.</summary>
public sealed class CopilotInferenceBudget(int maxModelCalls, long maxTotalTokens, DateTimeOffset deadline,
    Func<int, long, CancellationToken, Task> persistReservation, int initialCalls = 0, long initialTokens = 0)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _stopped;
    public DateTimeOffset Deadline { get; } = deadline;
    public int ModelCalls { get; private set; } = initialCalls;
    public long ChargedTokens { get; private set; } = initialTokens;
    public CopilotAdmissionStop? AdmissionStop { get; private set; }
    public bool TransportFailed { get; private set; }
    internal bool AdmitsTools => !_stopped && DateTimeOffset.UtcNow < Deadline;
    internal void RecordTransportFailure() => TransportFailed = true;
    public void CloseAdmissions() => _stopped = true;
    public async Task StopAsync(CancellationToken ct = default)
    {
        CloseAdmissions();
        await _gate.WaitAsync(ct);
        _gate.Release();
    }
    internal async Task ReserveAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_stopped) throw new InvalidOperationException("The bounded task no longer admits inference.");
            if (DateTimeOffset.UtcNow >= Deadline) throw ExhaustedBudget(CopilotAdmissionStopKind.Deadline);
            if (ModelCalls >= maxModelCalls) throw ExhaustedBudget(CopilotAdmissionStopKind.Calls);
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method != HttpMethod.Post || request.Content is null ||
                !(path.EndsWith("/chat/completions", StringComparison.Ordinal) || path.EndsWith("/responses", StringComparison.Ordinal)))
                throw new InvalidOperationException("Bounded Copilot inference requires a supported text request with an explicit output token ceiling.");
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject
                ?? throw new InvalidOperationException("Invalid bounded inference request.");
            if (body["previous_response_id"] is not null || body["conversation"] is not null || !TextOnly(body))
                throw new InvalidOperationException("Opaque conversation state and non-text inference cannot be charged safely.");
            // The supported byte-BPE text protocols consume at most one token per UTF-8 byte.
            // Charge all serialized metadata as well, plus an explicit framing allowance.
            var inputCeiling = checked(Encoding.UTF8.GetByteCount(body.ToJsonString()) + 4096L);
            var remaining = maxTotalTokens - ChargedTokens - inputCeiling;
            if (remaining < 1) throw ExhaustedBudget(CopilotAdmissionStopKind.Tokens, inputCeiling);
            var field = path.EndsWith("/responses", StringComparison.Ordinal) ? "max_output_tokens" : "max_completion_tokens";
            var requested = body[field]?.GetValue<long>() ?? body["max_tokens"]?.GetValue<long>() ?? 8192;
            if (requested < 1) throw new InvalidOperationException("Invalid inference output ceiling.");
            var outputCeiling = Math.Min(requested, Math.Min(remaining, 32768));
            body.Remove("max_tokens"); body[field] = outputCeiling;
            // Multiple completions would multiply the reserved output allowance.
            if (body["n"] is { } count && count.GetValue<int>() != 1)
                throw new InvalidOperationException("Bounded inference permits one completion per request.");
            ModelCalls++; ChargedTokens = checked(ChargedTokens + inputCeiling + outputCeiling);
            await persistReservation(ModelCalls, ChargedTokens, ct);
            var original = request.Content;
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            original.Dispose();
        }
        finally { _gate.Release(); }
    }

    private InvalidOperationException ExhaustedBudget(CopilotAdmissionStopKind kind, long? inputCeiling = null)
    {
        AdmissionStop ??= new(kind, maxModelCalls, maxTotalTokens, Deadline, ModelCalls, ChargedTokens, inputCeiling);
        _stopped = true;
        return new InvalidOperationException("The approved Copilot inference allowance cannot admit this request.");
    }

    private static bool TextOnly(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue kind && kind.TryGetValue<string>(out var type) &&
                type is "image_url" or "input_image" or "input_audio" or "input_file" or "file" or "computer_screenshot") return false;
            return obj.All(p => TextOnly(p.Value));
        }
        return node is not JsonArray array || array.All(TextOnly);
    }
}

internal sealed class CopilotLogicalInferenceHandler(CopilotInferenceBudget budget, CopilotInferenceProxyHandler? proxy) : CopilotRequestHandler
{
    private static readonly HttpClient Direct = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    protected override async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
    {
        await budget.ReserveAsync(request, context.CancellationToken);
        try
        {
            return proxy is null
                ? await Direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken)
                : await proxy.DispatchAsync(request, context);
        }
        catch { budget.RecordTransportFailure(); throw; }
    }
    protected override Task<CopilotWebSocketHandler> OpenWebSocketAsync(GitHub.Copilot.CopilotRequestContext context)
        => Task.FromException<CopilotWebSocketHandler>(new InvalidOperationException("Unmetered WebSocket inference is unavailable for bounded logical operations."));
}
