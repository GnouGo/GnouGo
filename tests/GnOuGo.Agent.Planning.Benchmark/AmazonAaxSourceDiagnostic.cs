using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Browser.Mcp;

// Separately approved source-only diagnostic. Deliberately absent from workflow
// policy capture, delegated execution, model facts and business-oracle dispatch.
internal sealed class AmazonAaxSourceDiagnostic
{
    internal const string Approval = "user-amazon-aax-source-only-20261111";
    internal const string Host = "aax-eu-zaz.amazon.fr";
    internal const string DestinationPattern = @"https://(?:www\.)?amazon\.fr/(?:s\?[^#\s]+|(?:[^/?#\s]+/)?dp/[A-Za-z0-9]{10}(?:[/?][^#\s]*)?|gp/product/[A-Za-z0-9]{10}(?:[/?][^#\s]*)?|gp/aw/d/[A-Za-z0-9]{10}(?:\?[^#\s]*)?|stores/page/[A-Fa-f0-9]{8}(?:-[A-Fa-f0-9]{4}){3}-[A-Fa-f0-9]{12}(?:\?[^#\s]+)?)";
    private static readonly Regex Entries = new(@"\Ahttps://aax-eu-zaz\.amazon\.fr/x/c/[^#\s]+\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex Destinations = new(@"\A(?:" + DestinationPattern + @")\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private readonly BrowserServerSettings _settings;
    private List<JsonObject> _entries = [];
    private HashSet<string> _originals = new(StringComparer.Ordinal);
    private HashSet<string> _snapshots = new(StringComparer.Ordinal);
    private int _cursor;

    internal AmazonAaxSourceDiagnostic(BrowserServerSettings settings, string approval)
    {
        if (approval != Approval || !settings.AllowedHosts.ToHashSet(StringComparer.Ordinal).SetEquals(["www.amazon.fr", "amazon.fr", "sponsored-ads.amazon.fr"]) ||
            !settings.AllowedNavigationUrlPatterns.SequenceEqual([AmazonStoreListPolicy.UrlPattern], StringComparer.Ordinal))
            throw new InvalidOperationException("The exact source-only grant and isolated original profile restrictions are required.");
        _settings = settings; Expire();
    }

    internal static bool IsEntry(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Fragment.Length == 0 && Entries.IsMatch(value);
    internal static bool IsDestination(string value) => Destinations.IsMatch(value) && AmazonStoreListPolicy.Allows(value);
    internal bool RedirectAllowed(string value) => IsDestination(value) || _originals.Contains(value);
    internal IReadOnlyList<JsonObject> OriginalOccurrences => _entries.Select(e => e.DeepClone().AsObject()).ToArray();
    internal string? Next => _cursor < _entries.Count ? _entries[_cursor]["href"]!.ToString() : null;

    internal void BeginSearch(string url)
    {
        Expire();
        if (!IsDestination(url) || new Uri(url).AbsolutePath != "/s") throw new InvalidOperationException("Only the approved search may acquire source authority.");
    }

    internal void ObserveSearch(JsonObject read, string query)
    {
        Expire();
        _entries = LiveWorkflowOracles.AaxDiagnosticSourceEntries(read, query);
        _originals = _entries.Select(e => e["href"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        _snapshots.Add(read["result"]!["observationSnapshot"]!["id"]!.ToString());
        // Replace the disposable host's lists atomically; never mutate saved
        // KeyVault settings. Every redirect is checked by the existing Browser
        // before contact against these literal observations, not a host prefix.
        _settings.AllowedNavigationUrlPatterns = [DestinationPattern, .. _originals.Select(Regex.Escape)];
        _settings.AllowedHosts = _originals.Count == 0 ? ["www.amazon.fr", "amazon.fr"] : ["www.amazon.fr", "amazon.fr", Host];
    }

    internal void RequireNext(string href)
    {
        if (Next is not { } next || next != href) throw new InvalidOperationException("Only the next exact original source occurrence is authorized.");
    }

    internal void ObserveResolution(JsonObject read)
    {
        var next = Next;
        if (next is null || read["arguments"]?["url"]?.ToString() != next || !LiveWorkflowOracles.AaxDiagnosticReadComplete(read, RedirectAllowed) ||
            read["result"]?["observationSnapshot"]?["id"]?.ToString() is not { } snapshot || !_snapshots.Add(snapshot))
        { Expire(); throw new InvalidOperationException("Incomplete or conflicting source evidence expires diagnostic authority."); }
        _cursor++;
    }

    internal void Expire()
    {
        _entries = []; _originals = new(StringComparer.Ordinal); _snapshots = new(StringComparer.Ordinal); _cursor = 0;
        _settings.AllowedNavigationUrlPatterns = [DestinationPattern];
        _settings.AllowedHosts = ["www.amazon.fr", "amazon.fr"];
    }
}

internal static partial class LiveWorkflowOracles
{
    internal static List<JsonObject> AaxDiagnosticSourceEntries(JsonObject read, string query)
    {
        var source = CompleteSnapshots([read]).SingleOrDefault();
        if (source is null || !WholeDocumentSearch(read) || !SearchAddress(source.Url) || string.IsNullOrWhiteSpace(query) ||
            SearchQuery(new Uri(source.Url!)) != query || read["arguments"]?["url"]?.ToString() is not { } requested ||
            !SearchAddress(requested) || SearchQuery(new Uri(requested)) != query ||
            read["result"]?["navigationEvidence"]?["finalUrl"]?.ToString() is not { } responseUrl ||
            !SearchAddress(responseUrl) || SearchQuery(new Uri(responseUrl)) != query || !AaxDiagnosticReadComplete(read, AmazonAaxSourceDiagnostic.IsDestination))
            throw new InvalidOperationException("A complete successful owned search is required for the source-only diagnostic.");
        var records = source.Records.OfType<JsonObject>().ToArray();
        var id = read["result"]?["observationSnapshot"]?["id"]?.ToString();
        if (id is null || records.Where((r, i) => r["reference"]?.ToString() != id + ":record:" + i).Any() || !ConsistentCardMetadata(records))
            throw new InvalidOperationException("Original source provenance is inconsistent.");
        var analysis = AnalyzeAmazonUnclassifiedHeadingContexts([read], query);
        var references = analysis["unclassified_heading_contexts"]!.AsArray().OfType<JsonObject>()
            .Where(c => c["asin_context"]?["value"]?.ToString() == "")
            .SelectMany(c => c["source_records"]!.AsArray()).Select(r => r!.ToString()).ToHashSet(StringComparer.Ordinal);
        return records.Where(r => references.Contains(r["reference"]!.ToString()) && AmazonAaxSourceDiagnostic.IsEntry(r["href"]!.ToString()))
            .Select(r => r.DeepClone().AsObject()).ToList();
    }

    internal static bool AaxDiagnosticReadComplete(JsonObject read, Func<string, bool> allowed)
        => read["error"]?.GetValue<bool>() == false && read["result"]?["success"]?.GetValue<bool>() == true &&
            CompleteSnapshots([read]).SingleOrDefault() is { } snapshot && ValidNavigationEvidence(read, allowed) &&
            read["result"]?["acquisition"]?["navigation"]?["url"]?.ToString() == snapshot.Url;
}
