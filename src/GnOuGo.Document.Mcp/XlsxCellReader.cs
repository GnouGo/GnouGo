using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace GnOuGo.Document.Mcp;

internal static class XlsxCellReader
{
    private const int MaxRowsPerSheet = 500;
    private const int MaxWorkbookCells = 100_000;

    internal static DocumentReadResult Read(string path, WorkbookPart? workbook)
    {
        if (workbook?.Workbook?.Sheets is not { } sheets)
            return DocumentReadResult.Error("READ_FAILED", "The workbook has no sheet directory.");
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ToArray() ?? [];
        var sections = new List<DocumentSection>();
        var remainingCells = MaxWorkbookCells;
        foreach (var sheet in sheets.Elements<Sheet>())
        {
            if (workbook.GetPartById(sheet.Id!) is not WorksheetPart worksheet) continue;
            var rows = new List<string?[]>();
            if (worksheet.Worksheet?.GetFirstChild<SheetData>() is not { } data)
                return DocumentReadResult.Error("READ_FAILED", "The worksheet has no cell data.");
            foreach (var row in data.Elements<Row>())
            {
                var rowNumber = row.RowIndex?.Value ?? (uint)(rows.Count + 1);
                if (rowNumber > MaxRowsPerSheet)
                    return DocumentReadResult.Error("READ_LIMIT_EXCEEDED", $"Cell output supports up to {MaxRowsPerSheet} rows per worksheet.");
                if (rowNumber <= rows.Count) throw new InvalidDataException("Worksheet rows are out of order.");
                while (rows.Count + 1 < rowNumber) rows.Add([]);
                var values = new List<string?>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = cell.CellReference?.Value is { } reference ? Column(reference, rowNumber) : values.Count + 1;
                    if (column <= values.Count || column > 16_384) throw new InvalidDataException("Invalid worksheet cell position.");
                    remainingCells -= column - values.Count;
                    if (remainingCells < 0)
                        return DocumentReadResult.Error("READ_LIMIT_EXCEEDED", $"Cell output supports up to {MaxWorkbookCells} cell positions per workbook.");
                    while (values.Count + 1 < column) values.Add(null);
                    values.Add(Value(cell, shared));
                }
                rows.Add(values.ToArray());
            }
            sections.Add(new DocumentSection($"xlsx:{sheet.SheetId?.Value ?? 0}", sheet.Name?.Value ?? "Sheet", null, "")
                { Rows = rows.ToArray() });
        }
        return DocumentReadResult.Ok(path, ".xlsx", sections);
    }

    private static int Column(string reference, uint rowNumber)
    {
        var column = 0;
        var i = 0;
        while (i < reference.Length && reference[i] is >= 'A' and <= 'Z')
            column = checked(column * 26 + reference[i++] - 'A' + 1);
        if (column == 0 || !uint.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row != rowNumber)
            throw new InvalidDataException("The cell reference does not match its worksheet row.");
        return column;
    }

    private static string? Value(Cell cell, SharedStringItem[] shared)
    {
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString is { } inline ? TextValue(inline) : null;
        var value = cell.CellValue?.InnerText;
        if (cell.DataType?.Value != CellValues.SharedString) return value;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || (uint)index >= (uint)shared.Length)
            throw new InvalidDataException("The cell references a missing shared string.");
        return TextValue(shared[index]);
    }

    private static string TextValue(RstType value) =>
        value.Text?.Text ?? string.Concat(value.Elements<Run>().Select(run => run.Text?.Text));
}
