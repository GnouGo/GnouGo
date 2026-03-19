using UglyToad.PdfPig.Content;

namespace DocIngestor.Core.Extractors;

/// <summary>
/// Small heuristics to detect "garbage" text pages (broken glyph mapping).
/// </summary>
internal static class PdfPigQualityHeuristics
{
    /// <summary>
    /// Default threshold used to decide when to fallback to a rendered page image.
    /// </summary>
    public const double DefaultMissingGlyphRatioThreshold = 0.25;

    public static (double missingRatio, int missingCount, int total) MissingGlyphStats(Page page)
    {
        var letters = page.Letters;
        if (letters is null || letters.Count == 0)
            return (0, 0, 0);

        int missing = 0;

        foreach (var l in letters)
        {
            var v = l.Value; // string

            // Some PDFs return "" when mapping is broken
            if (string.IsNullOrEmpty(v))
            {
                missing++;
                continue;
            }

            bool bad = false;
            foreach (var c in v)
            {
                if (IsBadChar(c))
                {
                    bad = true;
                    break;
                }
            }

            if (bad) missing++;
        }

        return (missing / (double)letters.Count, missing, letters.Count);
    }

    public static bool ShouldFallbackToRenderedPage(Page page, double threshold = DefaultMissingGlyphRatioThreshold)
    {
        var (ratio, _, total) = MissingGlyphStats(page);
        if (total == 0) return false;
        return ratio >= threshold;
    }

    private static bool IsBadChar(char c)
        => c == '\0' || c == '\uFFFD' || char.IsControl(c);
}
