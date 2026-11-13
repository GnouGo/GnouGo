using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

/// <summary>Adapts an explicitly selected wire format; the SDK still owns tools and permissions.</summary>
public abstract class CopilotInferenceAdapter
{
    public abstract Task<CopilotPreparedInference> PrepareAsync(
        HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context);
}

/// <summary>The actual HTTP request is admitted before sending; decoding grants no tool authority.</summary>
public abstract class CopilotPreparedInference : IDisposable
{
    public abstract HttpRequestMessage Request { get; }
    public abstract Task<HttpResponseMessage> RestoreResponseAsync(HttpResponseMessage response, CancellationToken ct);
    public virtual void Dispose() => Request.Dispose();
}

internal static class CopilotInferenceDispatch
{
    private static readonly HttpClient Direct = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage original,
        GitHub.Copilot.CopilotRequestContext context, CopilotInferenceAdapter? adapter,
        CopilotInferenceProxyHandler? proxy, Func<HttpRequestMessage, CancellationToken, Task>? reserve,
        Action? transportFailed)
    {
        using var prepared = adapter is null ? null : await adapter.PrepareAsync(original, context);
        var actual = prepared?.Request ?? original;
        if (reserve is not null) await reserve(actual, context.CancellationToken);
        HttpResponseMessage response;
        try
        {
            response = proxy is null
                ? await Direct.SendAsync(actual, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken)
                : await proxy.DispatchAsync(actual, context);
        }
        catch { transportFailed?.Invoke(); throw; }
        if (prepared is null) return response;
        try
        {
            var restored = await prepared.RestoreResponseAsync(response, context.CancellationToken);
            if (!ReferenceEquals(restored, response)) response.Dispose();
            return restored;
        }
        catch { response.Dispose(); throw; }
    }
}

internal sealed class CopilotAdaptedInferenceHandler(CopilotInferenceAdapter adapter, CopilotInferenceProxyHandler? proxy)
    : CopilotRequestHandler
{
    protected override Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request,
        GitHub.Copilot.CopilotRequestContext context)
        => CopilotInferenceDispatch.SendAsync(request, context, adapter, proxy, null, null);

    protected override Task<CopilotWebSocketHandler> OpenWebSocketAsync(GitHub.Copilot.CopilotRequestContext context)
        => Task.FromException<CopilotWebSocketHandler>(new InvalidOperationException("The selected inference adapter requires HTTP."));
}
