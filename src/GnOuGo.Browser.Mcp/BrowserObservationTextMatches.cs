using System.Text.Json;

namespace GnOuGo.Browser.Mcp;

public sealed record BrowserTextMatch(int Page, int Offset, string Kind, string Selector, string Text, string? Reference);

public sealed record BrowserObservationTextMatches(IReadOnlyList<BrowserTextMatch> Records, bool Truncated)
{
    internal const int MaxRecords = 64;
    private const int MaxRecordBytes = 16384;

    internal static BrowserObservationTextMatches Create(IReadOnlyList<BrowserObservation> pages, string[] prefixes)
    {
        var matches = new List<BrowserTextMatch>();
        var remaining = MaxRecordBytes;
        for (var page = 0; page < pages.Count; page++)
        {
            for (var offset = 0; offset < pages[page].Records.Count; offset++)
            {
                var record = pages[page].Records[offset];
                if (record.Kind is not ("heading" or "text") || string.IsNullOrWhiteSpace(record.Text) ||
                    !prefixes.Any(prefix => record.Selector.StartsWith(prefix, StringComparison.Ordinal))) continue;
                if (matches.Count == MaxRecords || record.Text.Length > remaining) return new(matches, true);
                var match = new BrowserTextMatch(page, offset, record.Kind, record.Selector, record.Text, record.Reference);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(match, BrowserMcpJsonContext.Default.BrowserTextMatch).Length;
                if (bytes > remaining) return new(matches, true);
                matches.Add(match);
                remaining -= bytes;
            }
        }
        return new(matches, false);
    }
}
