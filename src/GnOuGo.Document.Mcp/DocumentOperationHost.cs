using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Document.Mcp;

/// <summary>
/// Orchestrates document read/write operations using OpenXml and PdfPig.
/// All operations are file-based and policy-checked.
/// </summary>
public sealed class DocumentOperationHost
{
    private readonly DocumentPolicy _policy;
    private readonly ILogger<DocumentOperationHost> _logger;

    public DocumentOperationHost(DocumentPolicy policy, ILogger<DocumentOperationHost> logger)
    {
        _policy = policy;
        _logger = logger;
    }

    public DocumentPolicyInfo GetPolicy() => _policy.DescribePolicy();

    // ────────────────────────────────── READ ──────────────────────────────────

    /// <summary>
    /// Read a document and extract its text/markdown representation.
    /// </summary>
    public DocumentReadResult Read(string filePath, string? format)
    {
        var resolved = _policy.ResolveFilePath(filePath);

        if (!File.Exists(resolved))
            return DocumentReadResult.Error("FILE_NOT_FOUND", $"File not found: {resolved}");

        if (!_policy.IsExtensionAllowed(resolved))
            return DocumentReadResult.Error("EXTENSION_NOT_ALLOWED",
                $"Extension '{Path.GetExtension(resolved)}' is not in the allowed list.");

        var fi = new FileInfo(resolved);
        if (fi.Length > _policy.MaxFileSizeBytes)
            return DocumentReadResult.Error("FILE_TOO_LARGE",
                $"File is {fi.Length} bytes; max allowed is {_policy.MaxFileSizeBytes}.");

        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        var outputFormat = string.IsNullOrWhiteSpace(format) ? "markdown" : format.Trim().ToLowerInvariant();

        try
        {
            return ext switch
            {
                ".pdf" => ReadPdf(resolved, outputFormat),
                ".docx" => ReadDocx(resolved, outputFormat),
                ".xlsx" => ReadXlsx(resolved, outputFormat),
                ".pptx" => ReadPptx(resolved, outputFormat),
                ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml"
                    => ReadPlainText(resolved, outputFormat),
                _ => DocumentReadResult.Error("UNSUPPORTED_FORMAT",
                    $"No reader for extension '{ext}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read {FilePath}", resolved);
            return DocumentReadResult.Error("READ_FAILED", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// List files in a directory that match allowed extensions.
    /// </summary>
    public DocumentListResult ListFiles(string? directoryPath, bool recursive)
    {
        var resolved = string.IsNullOrWhiteSpace(directoryPath)
            ? _policy.DefaultWorkingDirectory
            : _policy.ResolveFilePath(directoryPath);

        if (!Directory.Exists(resolved))
            return new DocumentListResult(false, "DIRECTORY_NOT_FOUND",
                $"Directory not found: {resolved}", []);

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.GetFiles(resolved, "*", option)
            .Where(f => _policy.IsExtensionAllowed(f))
            .Select(f =>
            {
                var info = new FileInfo(f);
                return new DocumentFileInfo(
                    Path.GetRelativePath(resolved, f),
                    f,
                    Path.GetExtension(f).ToLowerInvariant(),
                    info.Length,
                    info.LastWriteTimeUtc);
            })
            .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DocumentListResult(true, null, null, files);
    }

    // ────────────────────────────────── WRITE ─────────────────────────────────

    /// <summary>
    /// Write text content to a file. Supports plain text, markdown, and simple DOCX generation.
    /// </summary>
    public DocumentWriteResult Write(string filePath, string content, string? encoding)
    {
        var resolved = _policy.ResolveFilePath(filePath);

        if (!_policy.IsExtensionAllowed(resolved))
            return DocumentWriteResult.Error("EXTENSION_NOT_ALLOWED",
                $"Extension '{Path.GetExtension(resolved)}' is not in the allowed list.");

        var ext = Path.GetExtension(resolved).ToLowerInvariant();

        try
        {
            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var enc = ResolveEncoding(encoding);

            switch (ext)
            {
                case ".pdf":
                    PdfWriter.WriteSimplePdf(resolved, content);
                    break;
                case ".docx":
                    DocxWriter.WriteSimpleDocx(resolved, content);
                    break;
                case ".xlsx":
                    XlsxWriter.WriteSimpleXlsx(resolved, content);
                    break;
                default:
                    File.WriteAllText(resolved, content, enc);
                    break;
            }

            var info = new FileInfo(resolved);
            _logger.LogInformation("Wrote {Bytes} bytes to {Path}", info.Length, resolved);

            return new DocumentWriteResult(true, resolved, info.Length, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write {FilePath}", resolved);
            return DocumentWriteResult.Error("WRITE_FAILED", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ────────────────────────────── READERS ───────────────────────────────────

    private static DocumentReadResult ReadPdf(string path, string format)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var sections = new List<DocumentSection>(pdf.NumberOfPages);

        foreach (var page in pdf.GetPages())
        {
            var words = UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor
                .NearestNeighbourWordExtractor.Instance
                .GetWords(page.Letters)
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ThenBy(w => w.BoundingBox.Left);

            var lines = GroupIntoLines(words);
            var text = string.Join("\n", lines);

            string output = format == "markdown"
                ? $"## Page {page.Number}\n\n{text}"
                : text;

            sections.Add(new DocumentSection($"page:{page.Number}", $"Page {page.Number}", page.Number, output));
        }

        return DocumentReadResult.Ok(path, ".pdf", sections);
    }

    private static DocumentReadResult ReadDocx(string path, string format)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return DocumentReadResult.Ok(path, ".docx", []);

        var sb = new StringBuilder();
        foreach (var el in body.ChildElements)
        {
            if (el is DocumentFormat.OpenXml.Wordprocessing.Paragraph p)
            {
                var heading = GetDocxHeadingLevel(p);
                var text = string.Join("", p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
                    .Select(t => t.Text));

                if (format == "markdown" && heading > 0)
                    sb.Append(new string('#', heading)).Append(' ');

                sb.AppendLine(text);
                if (format == "markdown") sb.AppendLine();
            }
            else if (el is DocumentFormat.OpenXml.Wordprocessing.Table t)
            {
                sb.AppendLine(RenderDocxTable(t, format));
                if (format == "markdown") sb.AppendLine();
            }
        }

        var section = new DocumentSection("docx:0", "document", null, sb.ToString().Trim());
        return DocumentReadResult.Ok(path, ".docx", [section]);
    }

    private static DocumentReadResult ReadXlsx(string path, string format)
    {
        using var xlsx = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, false);
        var wbPart = xlsx.WorkbookPart;
        var sst = wbPart?.SharedStringTablePart?.SharedStringTable;
        var shared = sst is null
            ? Array.Empty<string>()
            : sst.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>()
                .Select(s => s.InnerText ?? "").ToArray();

        var sheets = wbPart?.Workbook?.Sheets?
            .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().ToList()
            ?? [];

        var sections = new List<DocumentSection>(sheets.Count);

        foreach (var sheet in sheets)
        {
            var wsPart = (DocumentFormat.OpenXml.Packaging.WorksheetPart?)wbPart!.GetPartById(sheet.Id!);
            var data = wsPart?.Worksheet?.Elements<DocumentFormat.OpenXml.Spreadsheet.SheetData>().FirstOrDefault();
            if (data is null) continue;

            var grid = new List<List<string>>();
            foreach (var row in data.Elements<DocumentFormat.OpenXml.Spreadsheet.Row>().Take(500))
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                    cells.Add(ReadXlsxCell(cell, shared));
                if (cells.Count > 0) grid.Add(cells);
            }

            if (grid.Count == 0) continue;

            var cols = grid.Max(r => r.Count);
            foreach (var r in grid)
                while (r.Count < cols) r.Add("");

            string output;
            var sheetName = sheet.Name?.Value ?? "Sheet";

            if (format == "markdown")
            {
                var md = new StringBuilder();
                md.Append("# Sheet: ").AppendLine(sheetName).AppendLine();
                md.Append("| ").Append(string.Join(" | ", grid[0].Select(EscapeMdCell))).AppendLine(" |");
                md.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cols))).AppendLine(" |");
                foreach (var r in grid.Skip(1))
                    md.Append("| ").Append(string.Join(" | ", r.Select(EscapeMdCell))).AppendLine(" |");
                output = md.ToString().TrimEnd();
            }
            else
            {
                output = string.Join("\n", grid.Select(r => string.Join("\t", r.Select(s => s.Trim()))));
            }

            sections.Add(new DocumentSection(
                $"xlsx:{sheet.SheetId?.Value ?? 0}", sheetName, null, output));
        }

        return DocumentReadResult.Ok(path, ".xlsx", sections);
    }

    private static DocumentReadResult ReadPptx(string path, string format)
    {
        using var pres = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        var presPart = pres.PresentationPart;
        var slideIds = presPart?.Presentation?.SlideIdList?
            .Elements<DocumentFormat.OpenXml.Presentation.SlideId>().ToList() ?? [];

        var sections = new List<DocumentSection>(slideIds.Count);
        int slideNo = 1;

        foreach (var slideId in slideIds)
        {
            var relId = slideId.RelationshipId?.Value;
            if (string.IsNullOrEmpty(relId)) { slideNo++; continue; }

            var slidePart = (DocumentFormat.OpenXml.Packaging.SlidePart)presPart!.GetPartById(relId);
            var slide = slidePart.Slide;
            if (slide is null) { slideNo++; continue; }
            var lines = new List<string>();

            foreach (var p in slide.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>())
            {
                var line = string.Join(" ",
                    p.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                        .Select(t => (t.Text ?? "").Trim())
                        .Where(s => s.Length > 0));
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }

            string output;
            if (format == "markdown")
            {
                var md = new StringBuilder();
                md.Append("## Slide ").Append(slideNo).AppendLine().AppendLine();
                foreach (var line in lines)
                    md.Append("- ").AppendLine(line);
                output = md.ToString().TrimEnd();
            }
            else
            {
                output = string.Join("\n", lines);
            }

            sections.Add(new DocumentSection($"pptx:{slideNo}", $"Slide {slideNo}", slideNo, output));
            slideNo++;
        }

        return DocumentReadResult.Ok(path, ".pptx", sections);
    }

    private static DocumentReadResult ReadPlainText(string path, string format)
    {
        var text = File.ReadAllText(path);
        var section = new DocumentSection("text:0", Path.GetFileName(path), null, text);
        return DocumentReadResult.Ok(path, Path.GetExtension(path), [section]);
    }

    // ────────────────────────────── HELPERS ───────────────────────────────────

    private static List<string> GroupIntoLines(
        IOrderedEnumerable<UglyToad.PdfPig.Content.Word> words)
    {
        var list = words.ToList();
        if (list.Count == 0) return [];

        var heights = list.Select(w => w.BoundingBox.Height).Where(h => h > 0).OrderBy(h => h).ToArray();
        var medianH = heights.Length > 0 ? heights[heights.Length / 2] : 6.0;
        var lineTol = Math.Max(3.0, medianH * 0.60);

        var lines = new List<string>();
        var currentLine = new List<(double X, string Text)>();
        double? currentY = null;

        foreach (var w in list)
        {
            if (currentY is null) currentY = w.BoundingBox.Bottom;
            if (Math.Abs(w.BoundingBox.Bottom - currentY.Value) > lineTol)
            {
                if (currentLine.Count > 0)
                {
                    var line = string.Join(" ", currentLine.OrderBy(x => x.X).Select(x => x.Text)).Trim();
                    if (line.Length > 0) lines.Add(line);
                }
                currentLine.Clear();
                currentY = w.BoundingBox.Bottom;
            }
            currentLine.Add((w.BoundingBox.Left, w.Text));
        }

        if (currentLine.Count > 0)
        {
            var line = string.Join(" ", currentLine.OrderBy(x => x.X).Select(x => x.Text)).Trim();
            if (line.Length > 0) lines.Add(line);
        }

        return lines;
    }

    private static int GetDocxHeadingLevel(DocumentFormat.OpenXml.Wordprocessing.Paragraph p)
    {
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (string.IsNullOrWhiteSpace(style)) return 0;
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && style.Length > 7
            && int.TryParse(style.AsSpan(7), out var h))
            return Math.Clamp(h, 1, 6);
        if (style.StartsWith("Titre", StringComparison.OrdinalIgnoreCase) && style.Length > 5
            && int.TryParse(style.AsSpan(5), out var t))
            return Math.Clamp(t, 1, 6);
        return 0;
    }

    private static string ReadXlsxCell(
        DocumentFormat.OpenXml.Spreadsheet.Cell cell, string[] shared)
    {
        var v = cell.CellValue?.InnerText ?? "";
        if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString
            && int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Length)
            return shared[idx].Trim();
        return v.Trim();
    }

    private static string RenderDocxTable(DocumentFormat.OpenXml.Wordprocessing.Table t, string format)
    {
        var rows = t.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().ToList();
        if (rows.Count == 0) return string.Empty;

        var grid = new List<List<string>>();
        foreach (var row in rows)
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>())
                cells.Add(string.Join(" ",
                    cell.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(x => x.Text)));
            grid.Add(cells);
        }

        var cols = grid.Max(r => r.Count);
        foreach (var r in grid)
            while (r.Count < cols) r.Add("");

        if (format != "markdown")
            return string.Join("\n", grid.Select(r => string.Join("\t", r.Select(s => s.Trim()))));

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", grid[0].Select(EscapeMdCell))).AppendLine(" |");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cols))).AppendLine(" |");
        foreach (var r in grid.Skip(1))
            sb.Append("| ").Append(string.Join(" | ", r.Select(EscapeMdCell))).AppendLine(" |");
        return sb.ToString().TrimEnd();
    }

    private static string EscapeMdCell(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "\\|").Replace("\n", "<br/>").Trim();
    }

    private static Encoding ResolveEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
        return name.Trim().ToLowerInvariant() switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false),
            "utf-8-bom" => new UTF8Encoding(true),
            "ascii" => Encoding.ASCII,
            "latin1" or "iso-8859-1" => Encoding.Latin1,
            _ => new UTF8Encoding(false)
        };
    }
}



