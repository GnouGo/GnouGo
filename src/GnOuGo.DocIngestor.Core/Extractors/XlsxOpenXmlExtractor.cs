using System.Text;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Metadata;
using DocIngestor.Core.Models;
using DocIngestor.Core.Formatting;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIngestor.Core.Extractors;

public sealed class XlsxOpenXmlExtractor : IDocumentTextExtractor
{
    private readonly ILogger<XlsxOpenXmlExtractor> _logger;

    public XlsxOpenXmlExtractor(ILogger<XlsxOpenXmlExtractor>? logger = null)
    {
        _logger = logger ?? NullLogger<XlsxOpenXmlExtractor>.Instance;
    }

    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken ct = default)
    {
        return new ValueTask<ExtractedDocument>(Task.Run(() =>
        {
            var mime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            var meta = MetadataDefaults.FromSource(source, mime);

            source.Rewind();
            using var xlsx = SpreadsheetDocument.Open(source.Content, false);

            try
            {
                var p = xlsx.PackageProperties;
                if (!string.IsNullOrWhiteSpace(p.Title)) meta["title"] = p.Title!;
                if (!string.IsNullOrWhiteSpace(p.Creator)) meta["author"] = p.Creator!;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read XLSX package properties for '{FileName}'.", source.FileName);
            }

            var wbPart = xlsx.WorkbookPart;
            var sst = wbPart?.SharedStringTablePart?.SharedStringTable;
            var sharedStrings = sst is null
                ? Array.Empty<string>()
                : sst.Elements<SharedStringItem>().Select(ssi => ssi.InnerText ?? "").ToArray();

            var sheets = wbPart?.Workbook?.Sheets?.Elements<Sheet>().ToList()
                         ?? new List<Sheet>();

            var sections = new List<ExtractedSection>(sheets.Count);

            
foreach (var sheet in sheets)
{
    var worksheetPart = (WorksheetPart?)wbPart.GetPartById(sheet.Id!);
    var sheetData = worksheetPart?.Worksheet?.Elements<SheetData>().FirstOrDefault();
    if (sheetData is null) continue;

    // Build a simple rectangular grid (best effort): take the first 200 rows to avoid crazy sheets
    var grid = new List<List<string?>>();
    foreach (var row in sheetData.Elements<Row>().Take(200))
    {
        var cells = new List<string?>();
        foreach (var cell in row.Elements<Cell>())
        {
            cells.Add(ReadCell(cell, sharedStrings));
        }
        // keep even empty rows if they have any cell
        if (cells.Count > 0)
            grid.Add(cells);
    }

    if (grid.Count == 0)
        continue;

    var cols = grid.Max(r => r.Count);
    foreach (var r in grid)
        while (r.Count < cols) r.Add("");

    // CSV-like
    var csvSb = new StringBuilder();
    foreach (var r in grid)
        csvSb.AppendLine(string.Join(",", r.Select(TextNormalization.CsvEscape)));

    // Markdown table
    var mdSb = new StringBuilder();
    mdSb.Append("# Sheet: ").AppendLine(sheet.Name?.Value ?? "Sheet");
    mdSb.AppendLine();

    var header = grid[0];
    mdSb.Append("| ").Append(string.Join(" | ", header.Select(s => TextNormalization.EscapeMarkdownCell(s)))).Append(" |").AppendLine();
    mdSb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cols))).Append(" |").AppendLine();
    foreach (var r in grid.Skip(1))
        mdSb.Append("| ").Append(string.Join(" | ", r.Select(s => TextNormalization.EscapeMarkdownCell(s)))).Append(" |").AppendLine();

    var markdown = mdSb.ToString().TrimEnd();
    var plainText = TextNormalization.NormalizeWhitespaceForDisplay(string.Join("\n", grid.Select(r => string.Join(" ", r.Where(x => !string.IsNullOrWhiteSpace(x))))));

    sections.Add(new ExtractedSection(
        SectionId: $"xlsx:{sheet.SheetId?.Value ?? 0}",
        Title: sheet.Name?.Value ?? "sheet",
        PageNumber: null,
        Text: plainText,
        Metadata: new Dictionary<string, string>
        {
            ["sheet"] = sheet.Name?.Value ?? "",
            ["format"] = "markdown",
            ["source"] = "xlsx"
        }
    )
    {
        Markdown = markdown,
        CsvLike = csvSb.ToString().TrimEnd()
    });
}

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

    private static string ReadCell(Cell cell, string[] sharedStrings)
    {
        var v = cell.CellValue?.InnerText ?? "";

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (int.TryParse(v, out var idx) && idx >= 0 && idx < sharedStrings.Length)
                return sharedStrings[idx].Trim();
        }

        return v.Trim();
    }

    private static string MakeId(DocumentSource source)
        => $"{source.FileName}:{source.ComputeSha256().Substring(0, 12)}";
}
