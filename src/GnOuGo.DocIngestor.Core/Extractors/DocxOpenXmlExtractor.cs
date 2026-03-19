using System.Text;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Metadata;
using DocIngestor.Core.Models;
using DocIngestor.Core.Formatting;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocIngestor.Core.Extractors;

public sealed class DocxOpenXmlExtractor : IDocumentTextExtractor
{
    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken ct = default)
    {
        return new ValueTask<ExtractedDocument>(Task.Run(() =>
        {
            var mime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            var meta = MetadataDefaults.FromSource(source, mime);

            source.Rewind();
            using var doc = WordprocessingDocument.Open(source.Content, false);

            // core properties (best effort)
            try
            {
                var p = doc.PackageProperties;
                if (!string.IsNullOrWhiteSpace(p.Title)) meta["title"] = p.Title!;
                if (!string.IsNullOrWhiteSpace(p.Creator)) meta["author"] = p.Creator!;
                if (p.Created is not null) meta["docCreatedUtc"] = (p.Created.Value.Kind == DateTimeKind.Utc ? p.Created.Value : p.Created.Value.ToUniversalTime()).ToString("O");
                if (p.Modified is not null) meta["docModifiedUtc"] = (p.Modified.Value.Kind == DateTimeKind.Utc ? p.Modified.Value : p.Modified.Value.ToUniversalTime()).ToString("O");
            }
            catch { /* ignore */ }

            var body = doc.MainDocumentPart?.Document?.Body;
            var sbPlain = new StringBuilder();
var sbMd = new StringBuilder();

if (body is not null)
{
    foreach (var el in body.ChildElements)
    {
        if (el is Paragraph p)
        {
            var md = RenderParagraphMarkdown(p, out var plain);
            // Always emit a line for each paragraph (even empty ones)
            // to preserve intentional line breaks from the Word document.
            // Use \n\n so that chunkers can correctly detect paragraph boundaries.
            sbPlain.Append(plain).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(md))
            {
                sbMd.AppendLine(md);
                sbMd.AppendLine();
            }
            else
            {
                // Empty paragraph = intentional blank line in Markdown too
                sbMd.AppendLine();
            }
        }
        else if (el is Table t)
        {
            var md = RenderTableMarkdown(t, out var plain);
            if (!string.IsNullOrWhiteSpace(plain))
                sbPlain.Append(plain).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(md))
            {
                sbMd.AppendLine(md);
                sbMd.AppendLine();
            }
        }
    }
}

var plainText = TextNormalization.NormalizeWhitespaceForDisplay(sbPlain.ToString());
var markdown = sbMd.Length > 0 ? sbMd.ToString().Trim() : null;

var sections = new[]
{
    new ExtractedSection(
        SectionId: "docx:0",
        Title: "document",
        PageNumber: null,
        Text: plainText,
        Metadata: new Dictionary<string, string>
        {
            ["format"] = "markdown",
            ["source"] = "docx"
        }
    )
    {
        Markdown = markdown
    }
};

            var extracted = new ExtractedDocument(
                DocumentId: MakeId(source),
                SourceName: source.FileName,
                MimeType: mime,
                Sections: sections,
                Metadata: meta
            );

            return MetadataDefaults.WithSha256(extracted, source.Content);
        }, ct));
    }

    private static string MakeId(DocumentSource source)
        => $"{source.FileName}:{source.ComputeSha256().Substring(0, 12)}";


private static string RenderParagraphMarkdown(Paragraph p, out string plain)
{
    var sbPlain = new StringBuilder();
    var sbMd = new StringBuilder();

    int heading = GetHeadingLevel(p);
    if (heading > 0)
    {
        sbMd.Append(new string('#', heading)).Append(' ');
    }

    foreach (var run in p.Elements<Run>())
    {
        bool bold = run.RunProperties?.Bold != null && (run.RunProperties.Bold.Val == null || run.RunProperties.Bold.Val.Value);

        // Iterate over all child elements to capture both Text and Break (line break / Shift+Enter)
        foreach (var child in run.ChildElements)
        {
            if (child is Text textEl)
            {
                var txt = textEl.Text;
                if (string.IsNullOrEmpty(txt)) continue;

                sbPlain.Append(txt);
                if (bold) sbMd.Append("**").Append(txt.Trim()).Append("**");
                else sbMd.Append(txt);
            }
            else if (child is Break)
            {
                // Shift+Enter in Word produces a <w:br/> element — preserve it as a line break
                sbPlain.Append('\n');
                sbMd.Append("  \n"); // two trailing spaces = Markdown line break
            }
        }
    }

    var md = sbMd.ToString().TrimEnd();
    plain = sbPlain.ToString().TrimEnd();

    // If the paragraph is empty, return empty
    if (string.IsNullOrWhiteSpace(md) && string.IsNullOrWhiteSpace(plain))
        return string.Empty;

    return md;
}

private static string RenderTableMarkdown(Table t, out string plain)
{
    var rows = t.Elements<TableRow>().ToList();
    if (rows.Count == 0)
    {
        plain = string.Empty;
        return string.Empty;
    }

    List<List<string>> grid = new();
    foreach (var row in rows)
    {
        List<string> cells = new();
        foreach (var cell in row.Elements<TableCell>())
        {
            var cellText = string.Join(" ", cell.Descendants<Text>().Select(x => x.Text));
            cells.Add(cellText ?? "");
        }
        grid.Add(cells);
    }

    int cols = grid.Max(r => r.Count);
    for (int i = 0; i < grid.Count; i++)
    {
        while (grid[i].Count < cols) grid[i].Add("");
    }

    // Plain: TSV-ish
    var sbPlain = new StringBuilder();
    foreach (var row in grid)
        sbPlain.AppendLine(string.Join("\t", row.Select(s => s.Trim())));
    plain = sbPlain.ToString().TrimEnd();

    // Markdown table
    var sb = new StringBuilder();
    var header = grid[0];
    sb.Append("| ").Append(string.Join(" | ", header.Select(TextNormalization.EscapeMarkdownCell))).Append(" |").AppendLine();
    sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cols))).Append(" |").AppendLine();

    foreach (var row in grid.Skip(1))
    {
        sb.Append("| ").Append(string.Join(" | ", row.Select(TextNormalization.EscapeMarkdownCell))).Append(" |").AppendLine();
    }

    return sb.ToString().TrimEnd();
}

private static int GetHeadingLevel(Paragraph p)
{
    var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
    if (string.IsNullOrWhiteSpace(style)) return 0;

    // Common style ids: Heading1..Heading6, Titre1..Titre6
    if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && style.Length > 7 && int.TryParse(style.Substring(7), out var h))
        return Math.Clamp(h, 1, 6);

    if (style.StartsWith("Titre", StringComparison.OrdinalIgnoreCase) && style.Length > 5 && int.TryParse(style.Substring(5), out var t1))
        return Math.Clamp(t1, 1, 6);

    return 0;
}
}
