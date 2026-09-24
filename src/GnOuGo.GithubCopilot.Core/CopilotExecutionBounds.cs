using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

public sealed class CopilotSandboxRequiredException() : InvalidOperationException(
    "Bounded commands require an available mandatory host sandbox. Configure managed sandbox.enabled=true and sandbox.failIfUnavailable=true before approving command execution.");

/// <summary>One invocation's non-renewable ceilings. Reservations precede inference and are never refunded.</summary>
public sealed class CopilotExecutionBounds(int maxModelCalls, long maxTotalTokens, DateTimeOffset deadline,
    IReadOnlySet<string> tools, Func<int, long, CancellationToken, Task> persistReservation)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlySet<string> Tools { get; } = tools;
    public DateTimeOffset Deadline { get; } = deadline;
    public int ModelCalls { get; private set; }
    /// <summary>Conservative charged token ceiling, not measured model usage.</summary>
    public long ChargedTokens { get; private set; }
    public bool Exhausted { get; private set; }
    public string? ApprovedPrompt { get; init; }
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
    private int _turnStarted;
    private volatile bool _stopped;
    public bool Stopped => _stopped;
    /// <summary>Prevent further admissions and wait for any admitted reservation to become durable.</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        await _gate.WaitAsync(CancellationToken.None);
        _gate.Release();
    }
    internal void BeginTurn(string prompt)
    {
        if (ApprovedPrompt != prompt || Interlocked.Exchange(ref _turnStarted, 1) != 0)
            throw new InvalidOperationException("A bounded task permits one turn with its approved objective and inputs. Inspect the original receipt.");
    }

    internal async Task ReserveAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_stopped) throw new InvalidOperationException("The bounded task no longer admits inference.");
            if (DateTimeOffset.UtcNow >= Deadline || ModelCalls >= maxModelCalls)
                throw ExhaustedBudget();
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
            if (remaining < 1) throw ExhaustedBudget();
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

    private InvalidOperationException ExhaustedBudget()
    { Exhausted = true; return new("The approved Copilot inference budget is exhausted."); }

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

internal sealed class CopilotBoundedInferenceHandler(CopilotExecutionBounds bounds, CopilotInferenceProxyHandler? proxy) : CopilotRequestHandler
{
    private static readonly HttpClient Direct = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    protected override async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
    {
        await bounds.ReserveAsync(request, context.CancellationToken);
        return proxy is null
            ? await Direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken)
            : await proxy.DispatchAsync(request, context);
    }
    protected override Task<CopilotWebSocketHandler> OpenWebSocketAsync(GitHub.Copilot.CopilotRequestContext context)
        => Task.FromException<CopilotWebSocketHandler>(new InvalidOperationException("Unmetered WebSocket inference is unavailable for bounded tasks."));
}
