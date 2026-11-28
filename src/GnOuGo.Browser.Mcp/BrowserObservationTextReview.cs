using System.Text;
using System.Text.Json;

namespace GnOuGo.Browser.Mcp;

public sealed record BrowserVisibleTextPage(int Records, IReadOnlyList<string> Text);
public sealed record BrowserObservationTextReview(string SnapshotId, int RecordCount, IReadOnlyList<BrowserVisibleTextPage> Pages)
{
    public const int ByteLimit = 64 * 1024;

    public static BrowserObservationTextReview? Create(string id, IReadOnlyList<BrowserObservation> pages, int expectedCount)
    {
        if (pages.Count > BrowserServerSettings.ObservationPageCeiling || pages.Any(p => p.CaptureTruncated || p.Id != id) ||
            pages.Sum(p => p.Records.Count) != expectedCount) return null;
        var bytes = 0L;
        var result = new List<BrowserVisibleTextPage>(pages.Count);
        foreach (var page in pages)
        {
            var texts = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in page.Records)
            {
                if (string.IsNullOrWhiteSpace(record.Text) || !seen.Add(record.Text)) continue;
                bytes += Encoding.UTF8.GetByteCount(record.Text);
                if (bytes > ByteLimit) return null;
                texts.Add(record.Text);
            }
            result.Add(new(page.Records.Count, texts));
        }
        var review = new BrowserObservationTextReview(id, expectedCount, result);
        return JsonSerializer.SerializeToUtf8Bytes(review, BrowserMcpJsonContext.Default.BrowserObservationTextReview).Length <= ByteLimit ? review : null;
    }
}
