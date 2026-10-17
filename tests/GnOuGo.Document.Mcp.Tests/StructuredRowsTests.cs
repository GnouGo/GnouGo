using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Xunit;

namespace GnOuGo.Document.Mcp.Tests;

public sealed class StructuredRowsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gnougo-exact-rows-").FullName;
    private DocumentOperationHost Host(long limit = 50 * 1024 * 1024) => new(new DocumentPolicy(new()
        { DefaultWorkingDirectory = _root, MaxFileSizeBytes = limit }, _root), NullLogger<DocumentOperationHost>.Instance);
    private static string?[][] Values => [["name", "description", "price"],
        [" duplicate ", "one\tcomplete\nblock\r\né ` ${literal} \\ & < >", "001.2300"],
        [" duplicate ", null, "=1+1"], [], [null, "", "  "]];

    [Fact]
    public void StructuredCellsPreserveExactStringsNullPositionsAndAppendOrder()
    {
        var host = Host(); var result = host.Write("exact.xlsx", null, null, false, Values);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Path.Combine(_root, "exact.xlsx"), result.FilePathAbsolute);
        Assert.True(host.Write("exact.xlsx", null, null, true, [["last", "duplicate", null]]).Success);
        using var workbook = SpreadsheetDocument.Open(result.FilePathAbsolute!, false);
        Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(workbook, TestContext.Current.CancellationToken));
        var rows = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Row>().ToArray();
        var expected = Values.Append(["last", "duplicate", null]).ToArray();
        Assert.Equal(expected.Length, rows.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var cells = rows[i].Elements<Cell>().ToArray(); Assert.Equal(expected[i].Length, cells.Length);
            for (var j = 0; j < cells.Length; j++)
            {
                Assert.Null(cells[j].CellFormula);
                Assert.Equal(CellValues.String, cells[j].DataType!.Value);
                Assert.Equal(expected[i][j], cells[j].CellValue?.Text);
            }
        }
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("both")][InlineData("neither")][InlineData("null_row")][InlineData("xml")][InlineData("size")][InlineData("extension")]
    public void InvalidRowsDoNotAlterAnExistingFile(string failure)
    {
        var path = Path.Combine(_root, "exact.xlsx");
        File.WriteAllText(path, "untouched"); var host = Host(failure == "size" ? 4 : 50 * 1024 * 1024);
        string?[][]? rows = failure switch { "neither" => null, "null_row" => [null!], "xml" => [["invalid\u0001"]], _ => Values };
        var result = host.Write(failure == "extension" ? "new.txt" : "exact.xlsx", failure == "both" ? "legacy" : null, null, false, rows);
        Assert.False(result.Success); Assert.Equal("untouched", File.ReadAllText(path)); Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void EmptyRowOverheadIsValidatedBeforeCreatingDirectories()
    {
        var result = Host(4).Write("not-created/exact.xlsx", null, null, false, [[]]);
        Assert.False(result.Success); Assert.Equal("FILE_TOO_LARGE", result.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "not-created")));
    }

    [Fact]
    public async Task ActualMcpContractAndTransportWriteRowsWithoutFormattingInference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = Environment.GetEnvironmentVariable("GNOUGO_DOCUMENT_MCP_TEST_EXECUTABLE") ??
                Path.Combine(AppContext.BaseDirectory, "GnOuGo.Document.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : "")),
            WorkingDirectory = _root, Name = "exact-rows",
            EnvironmentVariables = new Dictionary<string, string?> { ["Document__DefaultWorkingDirectory"] = _root, ["OpenTelemetry__Enabled"] = "false" }
        }), cancellationToken: ct);
        var tool = Assert.Single(await client.ListToolsAsync(cancellationToken: ct), t => t.Name == "document_write");
        Assert.True(tool.JsonSchema.TryGetProperty("oneOf", out var alternatives)); Assert.Equal(2, alternatives.GetArrayLength());
        Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("rows", out _));
        var result = await client.CallToolAsync("document_write", new Dictionary<string, object?> { ["filePath"] = "observed.xlsx", ["rows"] = Values }, cancellationToken: ct);
        Assert.NotEqual(true, result.IsError);
        var path = result.StructuredContent!.Value.GetProperty("filePathAbsolute").GetString()!;
        using (var workbook = SpreadsheetDocument.Open(path, false))
            Assert.Equal(Values[1][1], workbook.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Row>().ElementAt(1).Elements<Cell>().ElementAt(1).CellValue!.Text);
        var before = await File.ReadAllBytesAsync(path, ct);
        var invalid = await client.CallToolAsync("document_write", new Dictionary<string, object?> { ["filePath"] = "observed.xlsx", ["content"] = "must not replace", ["rows"] = Values }, cancellationToken: ct);
        Assert.True(invalid.IsError); Assert.Equal(before, await File.ReadAllBytesAsync(path, ct));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
