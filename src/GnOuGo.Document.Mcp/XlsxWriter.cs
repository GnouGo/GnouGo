using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace GnOuGo.Document.Mcp;

/// <summary>
/// Generates a simple XLSX file from TSV/CSV-like text (tab or comma separated).
/// </summary>
internal static class XlsxWriter
{
    public static void WriteSimpleXlsx(string path, string content)
        => WriteRows(path, TextRows(content), preserve: false);

    internal static void WriteRows(string path, IEnumerable<string?[]> rows, bool preserve = true)
    {
        using var xlsx = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook, autoSave: false);
        var wbPart = xlsx.AddWorkbookPart();
        wbPart.Workbook = new Workbook();

        var wsPart = wbPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        wsPart.Worksheet = new Worksheet(sheetData);

        var sheets = wbPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = wbPart.GetIdOfPart(wsPart),
            SheetId = 1,
            Name = "Sheet1"
        });

        AppendRows(sheetData, rows, 1);

        SaveWorksheet(wsPart, preserve);
        wbPart.Workbook.Save();
    }

    public static void AppendSimpleXlsx(string path, string content)
        => AppendRows(path, TextRows(content), preserve: false);

    internal static void AppendRows(string path, IEnumerable<string?[]> rows, bool preserve = true)
    {
        using var xlsx = SpreadsheetDocument.Open(path, true, new OpenSettings { AutoSave = false });
        var wbPart = xlsx.WorkbookPart ?? xlsx.AddWorkbookPart();
        wbPart.Workbook ??= new Workbook();

        var sheet = wbPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault();
        WorksheetPart wsPart;
        if (sheet?.Id is not null)
        {
            wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
        }
        else
        {
            wsPart = wbPart.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new Worksheet(new SheetData());
            var sheets = wbPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = 1,
                Name = "Sheet1"
            });
        }

        wsPart.Worksheet ??= new Worksheet();
        var sheetData = wsPart.Worksheet.GetFirstChild<SheetData>()
            ?? wsPart.Worksheet.AppendChild(new SheetData());
        var nextRowIndex = sheetData.Elements<Row>()
            .Select(row => row.RowIndex?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U;

        AppendRows(sheetData, rows, nextRowIndex);

        SaveWorksheet(wsPart, preserve);
        wbPart.Workbook.Save();
    }

    private static void SaveWorksheet(WorksheetPart part, bool preserve)
    {
        var worksheet = part.Worksheet ?? throw new InvalidOperationException("The worksheet is missing.");
        if (!preserve) { worksheet.Save(); return; }
        // XML normalizes literal carriage returns when read. Entity-encode them
        // so XLSX cell values survive without a spreadsheet-specific escape DSL.
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        using var writer = System.Xml.XmlWriter.Create(stream, new() { NewLineHandling = System.Xml.NewLineHandling.Entitize });
        worksheet.WriteTo(writer);
    }

    private static IEnumerable<string?[]> TextRows(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Split by tab first; fall back to comma if no tabs found
            var separator = line.Contains('\t') ? '\t' : ',';
            yield return line.TrimEnd('\r').Split(separator).Select(v => v.Trim()).ToArray();
        }
    }

    private static void AppendRows(SheetData sheetData, IEnumerable<string?[]> rows, uint firstRowIndex)
    {
        var rowIndex = firstRowIndex;
        foreach (var values in rows)
        {
            var row = new Row { RowIndex = rowIndex };

            foreach (var val in values)
            {
                var cell = new Cell
                {
                    DataType = CellValues.String,
                    CellValue = val is null ? null : new CellValue(val) { Space = SpaceProcessingModeValues.Preserve }
                };
                row.Append(cell);
            }

            sheetData.Append(row);
            rowIndex++;
        }
    }
}
