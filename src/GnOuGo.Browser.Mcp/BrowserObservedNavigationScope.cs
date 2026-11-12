using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Browser.Mcp;

// A host-owned permission derived from a complete observation, never a product
// classifier or model-authored grant. Each Browser instance owns its own scope.
internal sealed class BrowserObservedNavigationScope
{
    private readonly object _sync = new();
    private readonly string[] _sources, _patterns;
    private readonly ILogger _logger;
    private HashSet<string> _entries = new(StringComparer.Ordinal);
    private long _generation;

    internal BrowserObservedNavigationScope(BrowserServerSettings settings, ILogger logger)
    {
        _logger = logger;
        _sources = settings.ObservedNavigationSourceUrlPatterns?.ToArray() ?? throw InvalidConfiguration();
        _patterns = settings.ObservedNavigationEntryUrlPatterns?.ToArray() ?? throw InvalidConfiguration();
        if ((_sources.Length == 0) != (_patterns.Length == 0)) throw InvalidConfiguration();
        try
        {
            foreach (var pattern in _sources.Concat(_patterns))
            {
                if (string.IsNullOrWhiteSpace(pattern)) throw InvalidConfiguration();
                _ = BrowserNavigationPolicy.Matches("", pattern);
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw InvalidConfiguration(); }
    }

    private static ArgumentException InvalidConfiguration() => new(
        "ObservedNavigationSourceUrlPatterns and ObservedNavigationEntryUrlPatterns must both be disabled or contain valid patterns.");
    internal bool IsSource(string? url) => url is not null && _sources.Any(p => BrowserNavigationPolicy.Matches(url, p));
    internal long Generation { get { lock (_sync) return _generation; } }
    internal void Clear(string reason = "failed_read")
    { lock (_sync) { _entries.Clear(); _generation++; Trace("cleared", reason); } }
    internal void BeginRead(string? url, string reason = "explicit_source_read") { if (IsSource(url)) Clear(reason); }
    internal void ObserveOwnedCommit(long generation) { lock (_sync) Trace("retained", "owned_source_commit", generation); }

    private void Trace(string outcome, string reason, long? expected = null, string? snapshot = null)
    {
        if (_sources.Length == 0) return;
        _logger.LogInformation("Observed navigation scope {Outcome}: reason={Reason}, generation={Generation}, expectedGeneration={ExpectedGeneration}, entries={EntryCount}, snapshot={SnapshotId}.",
            outcome, reason, _generation, expected, _entries.Count, snapshot);
        Activity.Current?.AddEvent(new ActivityEvent("browser.observed_navigation." + outcome, tags: new ActivityTagsCollection
        {
            ["browser.observed_navigation.reason"] = reason, ["browser.observed_navigation.generation"] = _generation,
            ["browser.observed_navigation.expected_generation"] = expected, ["browser.observed_navigation.entries"] = _entries.Count,
            ["browser.observed_navigation.snapshot_id"] = snapshot
        }));
    }

    internal Uri Validate(string url, BrowserServerSettings settings, bool explicitContentRead)
    {
        try { return BrowserNavigationPolicy.ValidateNavigationTarget(url, settings); }
        catch (InvalidOperationException) when (explicitContentRead && Contains(url))
        {
            // Reuse all syntax and literal/transmitted-form checks. Only this
            // exact observed entry gets additional authority, including its host.
            return BrowserNavigationPolicy.ValidateNavigationTarget(url, LiteralPolicy(url));
        }
    }

    private bool Contains(string url) { lock (_sync) return _entries.Contains(url); }
    private static BrowserServerSettings LiteralPolicy(string url) => new()
        { AllowedNavigationUrlPatterns = [Regex.Escape(url)] };

    internal void Publish(BrowserContentResult result, string? selector, long generation)
    {
        if (!IsSource(result.Url)) return;
        var evidence = result.NavigationEvidence;
        var snapshot = result.ObservationSnapshot;
        if (selector is not null || !result.Success || result.Truncated || result.Format != "observation_complete" ||
            snapshot is null || snapshot.CaptureTruncated || snapshot.ManifestTruncated ||
            evidence is not { Complete: true } || evidence.Hops.Count == 0 ||
            evidence.Hops.Any(h => h.Method != "GET") || evidence.Hops[^1].StatusCode is < 200 or >= 300 ||
            !IsSource(evidence.FinalUrl) || (evidence.DocumentUrl ?? evidence.FinalUrl) != result.Url ||
            result.Acquisition?.Navigation?.Url != result.Url ||
            snapshot.RecordCount != snapshot.Pages.Sum(p => p.Records.Count) ||
            snapshot.Pages.Any(p => p.Id != snapshot.Id || p.CaptureTruncated || p.NextCursor is not null))
        { lock (_sync) Trace("withheld", "source_evidence_incomplete", generation, snapshot?.Id); return; }
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in snapshot.Pages.SelectMany(p => p.Records))
        {
            if (record.Kind != "link" || record.Href is not { } href || !_patterns.Any(p => BrowserNavigationPolicy.Matches(href, p)) ||
                !Uri.TryCreate(href, UriKind.Absolute, out var uri) || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) continue;
            try
            {
                _ = BrowserNavigationPolicy.ValidateNavigationTarget(href, LiteralPolicy(href));
                entries.Add(href);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException) { }
        }
        lock (_sync)
        {
            if (_generation != generation) { Trace("withheld", "source_changed_during_acquisition", generation, snapshot.Id); return; }
            _entries = entries;
            Trace("published", "complete_owned_source", generation, snapshot.Id);
        }
    }
}
