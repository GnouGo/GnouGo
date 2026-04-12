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
    {
        using var xlsx = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
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

        var lines = content.Split('\n', StringSplitOptions.None);
        uint rowIndex = 1;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var row = new Row { RowIndex = rowIndex };
            // Split by tab first; fall back to comma if no tabs found
            var separator = line.Contains('\t') ? '\t' : ',';
            var values = line.TrimEnd('\r').Split(separator);

            foreach (var val in values)
            {
                var cell = new Cell
                {
                    DataType = CellValues.String,
                    CellValue = new CellValue(val.Trim())
                };
                row.Append(cell);
            }

            sheetData.Append(row);
            rowIndex++;
        }

        wbPart.Workbook.Save();
    }
}

