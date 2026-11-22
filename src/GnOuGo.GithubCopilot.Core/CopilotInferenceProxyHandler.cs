using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Routes every SDK inference request through an explicitly configured local policy host.</summary>
public class CopilotInferenceProxyHandler : CopilotRequestHandler
{
    public const string UpstreamHeader = "X-GnOuGo-Inference-Upstream";
    public const string RequestHeader = "X-GnOuGo-Inference-Request";
    public const string RetryOwnerHeader = "X-GnOuGo-Inference-Retry-Owner";
    public const string RetryOwner = "journal-v1";
    public const string AttemptHeader = "X-GnOuGo-Inference-Attempt";
    public const string NextAttemptHeader = "X-GnOuGo-Inference-Next-Attempt";
    internal static readonly HttpRequestOptionsKey<bool> RepeatedReservation = new("gnougo.copilot.repeat_reservation");
    private bool _journalOwnsRetries;
    private int _terminalStatus;
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    internal CopilotInferenceProxyHandler ForClient() => new(_client, _endpoint) { _journalOwnsRetries = _journalOwnsRetries };
    internal bool JournalOwnsRetries => _journalOwnsRetries;
    internal void StopAfterUncertainResponse()
    {
        if (_journalOwnsRetries) Interlocked.CompareExchange(ref _terminalStatus, -1, 0);
    }
    private readonly HttpClient _client;
    private readonly Uri _endpoint;

    public CopilotInferenceProxyHandler(HttpClient client, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("An inference policy proxy must be an absolute loopback HTTP endpoint without user information or a fragment.", nameof(endpoint));
        _client = client; _endpoint = endpoint;
    }

    public async Task VerifyReadyAsync(CancellationToken ct)
    {
        using var ready = await _client.PostAsync(_endpoint.AbsoluteUri.TrimEnd('/') + "/ready",
            new StringContent("sdk-http-interception-v1"), ct);
        ready.EnsureSuccessStatusCode();
        _journalOwnsRetries = ready.Headers.TryGetValues(RetryOwnerHeader, out var owners) && owners.SequenceEqual([RetryOwner]);
    }

    protected override Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
        => CopilotInferenceDispatch.SendAsync(request, context, null, this, null, null);

    internal async Task<HttpResponseMessage> DispatchAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context, CancellationToken ct, int attempt, Func<Task>? reserve)
    {
        if (!_journalOwnsRetries) return await ForwardAsync(request, context, ct, attempt);
        await _dispatch.WaitAsync(ct);
        try
        {
            var terminal = Volatile.Read(ref _terminalStatus);
            if (terminal != 0)
                throw new HttpRequestException("The inference journal stopped this SDK invocation after a terminal rejection or uncertain exchange.",
                    null, terminal > 0 ? (System.Net.HttpStatusCode)terminal : null);
            if (reserve is not null) await reserve();
            var response = await ForwardAsync(request, context, ct, attempt);
            if (!response.IsSuccessStatusCode && !(response.StatusCode == System.Net.HttpStatusCode.Conflict && response.Headers.Contains(NextAttemptHeader)))
                Interlocked.CompareExchange(ref _terminalStatus, (int)response.StatusCode, 0);
            return response;
        }
        catch
        {
            StopAfterUncertainResponse();
            throw;
        }
        finally { _dispatch.Release(); }
    }

    private async Task<HttpResponseMessage> ForwardAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context,
        CancellationToken ct, int attempt)
    {
        var upstream = request.RequestUri ?? throw new InvalidOperationException("Inference has no upstream URI.");
        using var forwarded = new HttpRequestMessage(request.Method, _endpoint);
        if (request.Content is { } original)
        {
            forwarded.Content = new ByteArrayContent(await original.ReadAsByteArrayAsync(ct));
            foreach (var header in original.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var header in request.Headers)
            if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && !header.Key.StartsWith("X-GnOuGo-Inference-", StringComparison.OrdinalIgnoreCase))
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        forwarded.Headers.TryAddWithoutValidation(UpstreamHeader, upstream.AbsoluteUri);
        forwarded.Headers.TryAddWithoutValidation(RequestHeader, context.RequestId);
        if (_journalOwnsRetries) forwarded.Headers.TryAddWithoutValidation(AttemptHeader, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // The policy host owns ceilings, receipts and forwarding. No direct fallback
        // or automatic HTTP retry can bypass an unavailable/rejecting policy host.
        return await _client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    protected override Task<CopilotWebSocketHandler> OpenWebSocketAsync(GitHub.Copilot.CopilotRequestContext context) =>
        Task.FromException<CopilotWebSocketHandler>(new InvalidOperationException("The configured inference policy proxy does not support unaccounted WebSocket inference."));
}
