using System.Text;
using System.Text.RegularExpressions;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Metadata;
using DocIngestor.Core.Models;
using DocIngestor.Core.Formatting;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DocIngestor.Core.Extractors;

public sealed class PdfPigExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken ct = default)
    {
        // PdfPig is synchronous; run in Task.Run so API callers are not blocked.
        return new ValueTask<ExtractedDocument>(Task.Run(() =>
        {
            var mime = "application/pdf";
            var meta = MetadataDefaults.FromSource(source, mime);

            source.Rewind();
            using var pdf = PdfDocument.Open(source.Content);

            // best-effort PDF metadata
            try
            {
                var info = pdf.Information;
                if (!string.IsNullOrWhiteSpace(info?.Title)) meta["title"] = info!.Title!;
                if (!string.IsNullOrWhiteSpace(info?.Author)) meta["author"] = info!.Author!;
                if (!string.IsNullOrWhiteSpace(info?.Creator)) meta["creator"] = info!.Creator!;
                if (!string.IsNullOrWhiteSpace(info?.Producer)) meta["producer"] = info!.Producer!;
            }
            catch { /* ignore */ }

            var sections = new List<ExtractedSection>(pdf.NumberOfPages);

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                var (ratio, missing, total) = PdfPigQualityHeuristics.MissingGlyphStats(page);

                var sectionMeta = new Dictionary<string, string>
                {
                    ["page"] = page.Number.ToString(),
                    ["format"] = "markdown",
                    ["source"] = "pdf",
                    ["pdfpig_missingGlyphRatio"] = ratio.ToString("0.000"),
                    ["pdfpig_missingGlyphCount"] = missing.ToString(),
                    ["pdfpig_letterCount"] = total.ToString(),
                };

                string displayText;
                string md;

                // If glyph mapping is too broken, fallback to a *rendered page image* placeholder.
                // This enables OCR downstream (via PdfPigImageExtractor + Skia render) without polluting embeddings with garbage.
                if (PdfPigQualityHeuristics.ShouldFallbackToRenderedPage(page))
                {
                    var placeholderId = $"pdf:{page.Number}:render:1";
                    displayText = $"[[PDF_IMAGE id={placeholderId}]]";
                    md = $"## Page {page.Number}\n\n{displayText}\n";

                    sectionMeta["pdfpig_fallbackRenderedPage"] = "true";
                    sectionMeta["pdfpig_fallbackImageId"] = placeholderId;
                }
                else
                {
                    var text = ExtractPageText(page);
                    displayText = TextNormalization.NormalizeWhitespaceForDisplay(text);
                    md = $"## Page {page.Number}\n\n{displayText}\n";
                }

                sections.Add(new ExtractedSection(
                    SectionId: $"page:{page.Number}",
                    Title: $"Page {page.Number}",
                    PageNumber: page.Number,
                    Text: displayText,
                    Metadata: sectionMeta
                ) { Markdown = md });
            }

            var doc = new ExtractedDocument(
                DocumentId: MakeId(source),
                SourceName: source.FileName,
                MimeType: mime,
                Sections: sections,
                Metadata: meta
            );

            return MetadataDefaults.WithSha256(doc, source.Content);
        }, ct));
    }

    private static string ExtractPageText(Page page)
    {
        var blocks = new List<(double Y, string Text)>();

        var letters = page.Letters;
        if (letters is not null && letters.Count > 0)
        {
            // 1) Convert letters -> words (PdfPig heuristic, usually better than manual gaps)
            var words = NearestNeighbourWordExtractor.Instance
                .GetWords(letters)
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => (Y: w.BoundingBox.Bottom, X: w.BoundingBox.Left, Text: CleanWord(w.Text)))
                .ToList();

            if (words.Count > 0)
            {
                // Dynamic line tolerance based on word height (more robust than const 2.5)
                var heights = NearestNeighbourWordExtractor.Instance.GetWords(letters)
                    .Select(w => w.BoundingBox.Height)
                    .Where(h => h > 0)
                    .OrderBy(h => h)
                    .ToArray();

                var medianH = heights.Length > 0 ? heights[heights.Length / 2] : 6.0;
                var lineTol = Math.Max(3.0, medianH * 0.60);

                // 2) Group words into lines
                var ordered = words
                    .OrderByDescending(w => w.Y)
                    .ThenBy(w => w.X)
                    .ToList();

                var current = new List<(double X, string Text)>(64);
                double? currentY = null;

                void FlushLine()
                {
                    if (current.Count == 0 || currentY is null) return;

                    var line = string.Join(" ", current.OrderBy(x => x.X).Select(x => x.Text));
                    line = FixSpacing(line).Trim();

                    if (!string.IsNullOrWhiteSpace(line))
                        blocks.Add((currentY.Value, line));

                    current.Clear();
                }

                foreach (var w in ordered)
                {
                    if (currentY is null) currentY = w.Y;

                    if (Math.Abs(w.Y - currentY.Value) > lineTol)
                    {
                        FlushLine();
                        currentY = w.Y;
                    }

                    current.Add((w.X, w.Text));
                }

                FlushLine();
            }
        }

        // ---- image placeholders (embedded images)
        int imgIdx = 0;
        foreach (var img in page.GetImages())
        {
            imgIdx++;
            var y = img.Bounds.Top;
            blocks.Add((y, $"[[PDF_IMAGE id=pdf:{page.Number}:img:{imgIdx}]]"));
        }

        if (blocks.Count == 0) return string.Empty;

        blocks.Sort((a, b) => b.Y.CompareTo(a.Y));

        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            if (string.IsNullOrWhiteSpace(b.Text)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(b.Text.TrimEnd());
        }

        return sb.ToString();
    }

    private static string CleanWord(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        // Avoid turning unknown glyphs into spaces. Keep visible text only.
        // Common “unknown” chars: NUL or replacement char.
        var chars = s.Where(ch =>
            ch != '\0' &&
            ch != '\uFFFD' &&
            !char.IsControl(ch)
        ).ToArray();

        return new string(chars);
    }

    private static string FixSpacing(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        // Add missing spaces after punctuation when followed by a letter (common PDF extraction artifact)
        s = Regex.Replace(s, @"([,;:!?\.])(?=\p{L})", "$1 ");

        // Add spaces between letter<->digit boundaries when missing
        s = Regex.Replace(s, @"(\p{L})(\d)", "$1 $2");
        s = Regex.Replace(s, @"(\d)(\p{L})", "$1 $2");

        return s;
    }

    private static string MakeId(DocumentSource source)
        => $"{source.FileName}:{source.ComputeSha256().Substring(0, 12)}";
}
