using System.Text.Json.Nodes;
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
    /// <summary>Narrow a supported request and its decoder to a final answer based on observed tools.</summary>
    public virtual bool TryRequireFinalResponse(JsonObject body) => false;
    public abstract Task<HttpResponseMessage> RestoreResponseAsync(HttpResponseMessage response, CancellationToken ct);
    public virtual void Dispose() => Request.Dispose();
}

internal static class CopilotInferenceDispatch
{
    private static readonly HttpClient Direct = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage original,
        GitHub.Copilot.CopilotRequestContext context, CopilotInferenceAdapter? adapter,
        CopilotInferenceProxyHandler? proxy, Func<HttpRequestMessage, CopilotPreparedInference?, CancellationToken, Task>? reserve,
        Action? transportFailed, TimeSpan? attemptTimeout = null)
    {
        using var prepared = adapter is null ? null : await adapter.PrepareAsync(original, context);
        var actual = prepared?.Request ?? original;
        if (reserve is not null) await reserve(actual, prepared, context.CancellationToken);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(270));
        HttpResponseMessage? response = null;
        var streaming = false;
        try
        {
            response = proxy is null
                ? await Direct.SendAsync(actual, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                : await proxy.DispatchAsync(actual, context, deadline.Token);
            if (prepared is not null)
            {
                var restored = await prepared.RestoreResponseAsync(response, deadline.Token);
                if (!ReferenceEquals(restored, response))
                {
                    response.Dispose();
                    return restored;
                }
            }
            var content = response.Content;
            var body = await content.ReadAsStreamAsync(deadline.Token);
            var bounded = new StreamContent(new CopilotInferenceStream(body, content, deadline, transportFailed));
            foreach (var header in content.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = bounded;
            streaming = true;
            return response;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            if (response is null || ex is HttpRequestException or OperationCanceledException or IOException)
                transportFailed?.Invoke();
            throw;
        }
        finally
        {
            if (!streaming) deadline.Dispose();
        }
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
