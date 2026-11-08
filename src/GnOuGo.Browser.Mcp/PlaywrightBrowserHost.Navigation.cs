using System.Diagnostics;
using Microsoft.Playwright;

namespace GnOuGo.Browser.Mcp;

public sealed record BrowserNavigationHop(string Url, string Method, int StatusCode, string? Location);
public sealed record BrowserNavigationEvidence(string RequestedUrl, string? FinalUrl,
    IReadOnlyList<BrowserNavigationHop> Hops, bool Complete)
{
    // History changes the document address without creating an HTTP response.
    public string? DocumentUrl { get; init; }
}

internal sealed class BrowserNavigationException(string code, string message, BrowserNavigationEvidence evidence)
    : InvalidOperationException(message)
{
    internal string Code { get; } = code;
    internal BrowserNavigationEvidence Evidence { get; } = evidence;
}

public sealed partial class PlaywrightBrowserHost
{
    private const int MaximumRedirects = 20;
    private NavigationRead? _navigationRead;
    private BrowserNavigationException? _navigationFailure;
    private BrowserNavigationEvidence? _lastNavigationEvidence;
    private IRequest? _pendingMainNavigation;
    private IRequest? _committedMainNavigation;

    private sealed class NavigationRead(IPage page, string requestedUrl, float timeout, CancellationToken cancellation)
    {
        internal IPage Page { get; } = page;
        internal string RequestedUrl { get; } = requestedUrl;
        internal string CurrentUrl { get; set; } = requestedUrl;
        internal string? NextUrl { get; set; }
        internal List<BrowserNavigationHop> Hops { get; } = [];
        internal Stopwatch Timer { get; } = Stopwatch.StartNew();
        internal CancellationToken Cancellation { get; } = cancellation;
        internal float Remaining => Math.Max(1, timeout - (float)Timer.Elapsed.TotalMilliseconds);
        internal BrowserNavigationEvidence Evidence(string? finalUrl = null) => new(RequestedUrl, finalUrl, Hops.ToArray(), finalUrl is not null);
    }

    private bool HasNavigationPolicy => _settings.AllowedHosts.Count > 0 || _settings.AllowedNavigationUrlPatterns.Count > 0;

    // Playwright routing sees only the first request of an automatic redirect chain.
    // Fetch that response once, without redirects/retries, then abort a redirect
    // before another destination can be contacted. Only an explicit GET read
    // continues it, as another validated browser navigation under the same deadline.
    private async Task GuardNavigationAsync(IRoute route)
    {
        var request = route.Request;
        if (!request.IsNavigationRequest) { await route.ContinueAsync(); return; }
        var read = _navigationRead;
        var owned = read is not null && request.Frame == read.Page.MainFrame && request.Method == "GET" &&
            request.Url == new Uri(read.CurrentUrl).GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        if (!owned && !HasNavigationPolicy) { await route.ContinueAsync(); return; }
        try
        {
            try { BrowserNavigationPolicy.ValidateNavigationTarget(request.Url, _settings); }
            catch (InvalidOperationException)
            {
                throw new BrowserNavigationException("NAVIGATION_POLICY_DENIED", "Navigation is outside the configured browser policy.",
                    read?.Evidence() ?? new(request.Url, null, [], false));
            }
            read?.Cancellation.ThrowIfCancellationRequested();
            var response = await route.FetchAsync(new() { MaxRedirects = 0, MaxRetries = 0,
                Timeout = owned ? read!.Remaining : _settings.NavigationTimeoutMs });
            try
            {
                response.Headers.TryGetValue("location", out var location);
                var hop = new BrowserNavigationHop(request.Url, request.Method, response.Status, location);
                if (owned) read!.Hops.Add(hop);
                if (response.Status is 301 or 302 or 303 or 307 or 308 && location is not null)
                {
                    var evidence = owned ? read!.Evidence() : new BrowserNavigationEvidence(request.Url, null, [hop], false);
                    if (!Uri.TryCreate(new Uri(request.Url), location, out var next))
                        throw new BrowserNavigationException("NAVIGATION_REDIRECT_INVALID", "The observed redirect has no valid destination.", evidence);
                    try { BrowserNavigationPolicy.ValidateNavigationTarget(next.AbsoluteUri, _settings); }
                    catch (InvalidOperationException)
                    { throw new BrowserNavigationException("NAVIGATION_POLICY_DENIED", "The observed redirect destination is outside the configured browser policy.", evidence); }
                    if (!owned)
                        throw new BrowserNavigationException("NAVIGATION_REDIRECT_REQUIRES_EXPLICIT_READ", "An observed redirect requires an explicit content read; no interaction is replayed.", evidence);
                    read!.NextUrl = next.AbsoluteUri;
                    await route.AbortAsync("aborted");
                }
                else
                {
                    read?.Cancellation.ThrowIfCancellationRequested();
                    await route.FulfillAsync(new() { Response = response });
                }
            }
            finally { await response.DisposeAsync(); }
        }
        catch (Exception error) when (error is BrowserNavigationException or PlaywrightException or TimeoutException or OperationCanceledException)
        {
            if (ReferenceEquals(_navigationRead, read))
                _navigationFailure = error as BrowserNavigationException ?? new("NAVIGATION_FAILED",
                    "Browser navigation did not complete; no final destination is established.", read?.Evidence() ?? new(request.Url, null, [], false));
            try { await route.AbortAsync("aborted"); }
            catch (PlaywrightException) { /* The caller may already have closed the cancelled page. */ }
        }
    }

    private async Task<IResponse?> NavigateReadAsync(IPage page, string url, WaitUntilState waitUntil, float timeout, CancellationToken ct)
    {
        var initial = BrowserNavigationPolicy.ValidateNavigationTarget(url, _settings).AbsoluteUri;
        var sameDocument = new Uri(initial).Fragment.Length > 0 && Uri.TryCreate(page.Url, UriKind.Absolute, out var previous) &&
            previous.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped) == new Uri(initial).GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        var read = new NavigationRead(page, initial, timeout, ct);
        _navigationFailure = null; _lastNavigationEvidence = null; _navigationRead = read;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (read.Timer.Elapsed.TotalMilliseconds >= timeout) throw new TimeoutException();
                if (!visited.Add(read.CurrentUrl))
                    throw new BrowserNavigationException("NAVIGATION_REDIRECT_LOOP", "The observed redirect chain contains a loop.", read.Evidence());
                if (visited.Count > MaximumRedirects + 1)
                    throw new BrowserNavigationException("NAVIGATION_REDIRECT_LIMIT", "The observed redirect chain exceeds the physical navigation limit.", read.Evidence());
                read.NextUrl = null;
                IResponse? response = null;
                try { response = await page.GotoAsync(read.CurrentUrl, new() { WaitUntil = waitUntil, Timeout = read.Remaining }); }
                catch (PlaywrightException) when (read.NextUrl is not null || _navigationFailure is not null) { }
                ct.ThrowIfCancellationRequested();
                ThrowNavigationFailure();
                if (read.NextUrl is { } next) { read.CurrentUrl = next; continue; }
                if (sameDocument && response is null && read.Hops.Count == 0) return null;
                if (response is null || response.Request.Method != "GET" || read.Hops.Count == 0 ||
                    response.Url != read.Hops[^1].Url)
                    throw new BrowserNavigationException("NAVIGATION_EVIDENCE_INCOMPLETE", "The final document cannot be bound to this navigation's observed responses.", read.Evidence());
                lock (_observationLock)
                {
                    if (_committedMainNavigation != response.Request)
                        throw new BrowserNavigationException("NAVIGATION_EVIDENCE_INCOMPLETE", "The observed response no longer owns the current document.", read.Evidence());
                    BrowserNavigationPolicy.ValidateNavigationTarget(page.Url, _settings);
                    if (page.Url != response.Url && (_lastNavigationEvidence?.FinalUrl != response.Url || _lastNavigationEvidence.DocumentUrl != page.Url))
                        throw new BrowserNavigationException("NAVIGATION_EVIDENCE_INCOMPLETE", "The document address cannot be bound to its committed response.", read.Evidence());
                    _lastNavigationEvidence = read.Evidence(response.Url) with { DocumentUrl = page.Url == response.Url ? null : page.Url };
                }
                return response;
            }
        }
        catch (OperationCanceledException error)
        {
            error.Data[nameof(BrowserNavigationEvidence)] = read.Evidence();
            throw;
        }
        finally { _navigationRead = null; }
    }

    private void ThrowNavigationFailure()
    {
        if (_navigationFailure is { } failure) { _navigationFailure = null; throw failure; }
    }

    private static BrowserNavigationEvidence? CommittedNavigationEvidence(IRequest? request, string finalUrl,
        IReadOnlyDictionary<IRequest, BrowserNavigationHop> responses)
    {
        if (request is null || request.Url != finalUrl) return null;
        var hops = new List<BrowserNavigationHop>();
        var visited = new HashSet<IRequest>();
        for (var current = request; current is not null; current = current.RedirectedFrom)
        {
            if (hops.Count > MaximumRedirects || !visited.Add(current) || !responses.TryGetValue(current, out var hop)) return null;
            hops.Add(hop);
        }
        hops.Reverse();
        return new(hops[0].Url, finalUrl, hops, true);
    }

    private BrowserNavigationEvidence? NavigationEvidenceFor(IPage page) =>
        (_lastNavigationEvidence?.DocumentUrl ?? _lastNavigationEvidence?.FinalUrl) == page.Url ? _lastNavigationEvidence : null;
}
