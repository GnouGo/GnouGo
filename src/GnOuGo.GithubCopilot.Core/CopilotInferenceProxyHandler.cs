using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Routes every SDK inference request through an explicitly configured local policy host.</summary>
public class CopilotInferenceProxyHandler : CopilotRequestHandler
{
    public const string UpstreamHeader = "X-GnOuGo-Inference-Upstream";
    public const string RequestHeader = "X-GnOuGo-Inference-Request";
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

    protected override async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
    {
        var upstream = request.RequestUri ?? throw new InvalidOperationException("Inference has no upstream URI.");
        using var forwarded = new HttpRequestMessage(request.Method, _endpoint) { Content = request.Content };
        foreach (var header in request.Headers)
            if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals(UpstreamHeader, StringComparison.OrdinalIgnoreCase) && !header.Key.Equals(RequestHeader, StringComparison.OrdinalIgnoreCase))
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        forwarded.Headers.TryAddWithoutValidation(UpstreamHeader, upstream.AbsoluteUri);
        forwarded.Headers.TryAddWithoutValidation(RequestHeader, context.RequestId);
        // The policy host owns ceilings, receipts and forwarding. No direct fallback
        // or automatic HTTP retry can bypass an unavailable/rejecting policy host.
        return await _client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken).ConfigureAwait(false);
    }

    protected override Task<CopilotWebSocketHandler> OpenWebSocketAsync(GitHub.Copilot.CopilotRequestContext context) =>
        Task.FromException<CopilotWebSocketHandler>(new InvalidOperationException("The configured inference policy proxy does not support unaccounted WebSocket inference."));
}
