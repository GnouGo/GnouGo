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
        // A negotiated journal applies the physical timer independently to each upstream send.
        // The SDK invocation cancellation still bounds the entire exchange, including its waits.
        if (proxy?.JournalOwnsRetries != true || attemptTimeout.HasValue)
            deadline.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(270));
        HttpResponseMessage? response = null;
        var streaming = false;
        void RecordFailure()
        {
            proxy?.StopAfterUncertainResponse();
            transportFailed?.Invoke();
        }
        try
        {
            if (proxy is null) response = await Direct.SendAsync(actual, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            else
            {
                for (var attempt = 1; ; attempt++)
                {
                    response = await proxy.DispatchAsync(actual, context, deadline.Token, attempt);
                    if (!proxy.JournalOwnsRetries || response.StatusCode != System.Net.HttpStatusCode.Conflict ||
                        !response.Headers.TryGetValues(CopilotInferenceProxyHandler.NextAttemptHeader, out var next)) break;
                    if (attempt >= 3 || !next.SequenceEqual([(attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)]))
                        throw new InvalidDataException("Invalid retry admission challenge from the inference journal.");
                    response.Dispose(); response = null;
                    if (reserve is null) continue;
                    actual.Options.Set(CopilotInferenceProxyHandler.RepeatedReservation, true);
                    await reserve(actual, null, deadline.Token);
                }
            }
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
            var bounded = new StreamContent(new CopilotInferenceStream(body, content, deadline, RecordFailure));
            foreach (var header in content.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = bounded;
            streaming = true;
            return response;
        }
        catch (Exception ex)
        {
            response?.Dispose();
            proxy?.StopAfterUncertainResponse();
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
