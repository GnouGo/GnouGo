using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GnOuGo.Document.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GnOuGo.Document.Mcp.Tests;

public class DocumentOperationHostTests
{
    [Fact]
    public void Read_NonExistentFile_ReturnsFileNotFound()
    {
        var host = CreateHost();

        var result = host.Read("nonexistent.txt", null);

        Assert.False(result.Success);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Read_DisallowedExtension_ReturnsExtensionNotAllowed()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "test.exe");
        File.WriteAllText(file, "data");
        var host = CreateHost(root);

        var result = host.Read("test.exe", null);

        Assert.False(result.Success);
        Assert.Equal("EXTENSION_NOT_ALLOWED", result.ErrorCode);
    }

    [Fact]
    public void Read_PlainTextFile_ReturnsContent()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "hello.txt"), "Hello, World!");
        var host = CreateHost(root);

        var result = host.Read("hello.txt", null);

        Assert.True(result.Success);
        Assert.Single(result.Sections);
        Assert.Contains("Hello, World!", result.Sections[0].Content);
    }

    [Fact]
    public void Read_MarkdownFile_ReturnsContent()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "readme.md"), "# Title\n\nSome content.");
        var host = CreateHost(root);

        var result = host.Read("readme.md", "plain");

        Assert.True(result.Success);
        Assert.Single(result.Sections);
        Assert.Contains("# Title", result.Sections[0].Content);
    }

    [Fact]
    public void Read_CsvFile_ReturnsContent()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "data.csv"), "name,age\nAlice,30\nBob,25");
        var host = CreateHost(root);

        var result = host.Read("data.csv", null);

        Assert.True(result.Success);
        Assert.Contains("Alice", result.Sections[0].Content);
    }

    [Fact]
    public void Write_PlainTextFile_CreatesFileAndReturnsSuccess()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.Write("output.txt", "Hello from MCP!", null);

        Assert.True(result.Success);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal("Hello from MCP!", File.ReadAllText(result.FilePath));
    }

    [Fact]
    public void Write_DisallowedExtension_ReturnsError()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.Write("output.exe", "data", null);

        Assert.False(result.Success);
        Assert.Equal("EXTENSION_NOT_ALLOWED", result.ErrorCode);
    }

    [Fact]
    public void Write_DocxFile_CreatesValidDocx()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("test.docx", "Line 1\nLine 2\nLine 3", null);

        Assert.True(writeResult.Success);
        Assert.True(File.Exists(writeResult.FilePath));

        // Read it back
        var readResult = host.Read("test.docx", "plain");
        Assert.True(readResult.Success);
        Assert.Contains("Line 1", readResult.Sections[0].Content);
        Assert.Contains("Line 2", readResult.Sections[0].Content);
    }

    [Fact]
    public void Write_DocxFile_WhenContentLooksLikeMarkdown_AppliesWordStyles()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);
        var markdown = """
            # Report title

            This has **bold** and *italic* text with `code`.

            - First bullet
            - Second bullet

            1. First step
            2. Second step

            > Important note
            """;

        var writeResult = host.Write("markdown.docx", markdown, null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);

        var paragraphs = body!.Elements<Paragraph>().ToArray();
        Assert.Contains(paragraphs, p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "Heading1"
            && p.InnerText.Contains("Report title", StringComparison.Ordinal));
        Assert.Contains(paragraphs, p => p.Descendants<Bold>().Any()
            && p.InnerText.Contains("bold", StringComparison.Ordinal));
        Assert.Contains(paragraphs, p => p.Descendants<Italic>().Any()
            && p.InnerText.Contains("italic", StringComparison.Ordinal));
        Assert.Contains(paragraphs, p => p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value == 1
            && p.InnerText.Contains("First bullet", StringComparison.Ordinal));
        Assert.Contains(paragraphs, p => p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value == 2
            && p.InnerText.Contains("First step", StringComparison.Ordinal));
        Assert.Contains(paragraphs, p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "Quote"
            && p.InnerText.Contains("Important note", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_DocxFile_WhenMarkdownContainsTable_CreatesWordTable()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);
        var markdown = """
            | Name | Age |
            | --- | --- |
            | Alice | 30 |
            | Bob | 25 |
            """;

        var writeResult = host.Write("table.docx", markdown, null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var table = document.Body!.Elements<Table>().SingleOrDefault();
        Assert.NotNull(table);
        Assert.Contains("Alice", table!.InnerText, StringComparison.Ordinal);
        Assert.Equal(3, table.Elements<TableRow>().Count());
    }

    [Fact]
    public void Write_DocxFile_WhenContentOnlyHasBoldMarkdown_AppliesBold()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("bold.docx", "Un **texte** important.", null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);

        var paragraph = Assert.Single(body!.Elements<Paragraph>());
        Assert.Contains(paragraph.Descendants<Run>(), run =>
            run.InnerText == "texte" && run.RunProperties?.Bold is not null);
        Assert.DoesNotContain("**", paragraph.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_DocxFile_WhenMarkdownContainsLink_CreatesHyperlink()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("link.docx", "Voir [GnOuGo](https://example.com/gnougo).", null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);
        var hyperlink = body!
            .Descendants<Hyperlink>()
            .SingleOrDefault();

        Assert.NotNull(hyperlink);
        Assert.Equal("GnOuGo", hyperlink!.InnerText);
        Assert.DoesNotContain("[", body.InnerText, StringComparison.Ordinal);
        Assert.Contains(doc.MainDocumentPart.HyperlinkRelationships,
            relationship => relationship.Uri.ToString() == "https://example.com/gnougo");
    }

    [Fact]
    public void Write_DocxFile_WhenMarkdownContainsHorizontalRule_CreatesParagraphBorder()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("rule.docx", "Intro\n\n---\n\nOutro", null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);
        var rule = body!
            .Elements<Paragraph>()
            .SingleOrDefault(p => p.ParagraphProperties?.ParagraphBorders?.BottomBorder is not null);

        Assert.NotNull(rule);
    }

    [Fact]
    public void Write_DocxFile_WhenMarkdownTableContainsInlineMarkdown_FormatsCellContent()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);
        var markdown = """
            | Name | Link |
            | --- | --- |
            | **Alice** | [Site](https://example.com) |
            """;

        var writeResult = host.Write("table-inline.docx", markdown, null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);
        var table = body!.Elements<Table>().Single();

        Assert.Contains(table.Descendants<Run>(), run =>
            run.InnerText == "Alice" && run.RunProperties?.Bold is not null);
        Assert.Contains(table.Descendants<Hyperlink>(), hyperlink => hyperlink.InnerText == "Site");
    }

    [Fact]
    public void Write_DocxFile_WhenMarkdownContainsSlimFaasSources_CreatesBothLinks()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);
        var markdown = "Sources : [https://slimfaas.dev](https://slimfaas.dev) - Projet CNCF Sandbox - GitHub : [SlimPlanet/SlimFaas](https://github.com/SlimPlanet/SlimFaas)";

        var writeResult = host.Write("sources.docx", markdown, null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);

        var links = body!.Descendants<Hyperlink>().ToArray();
        Assert.Equal(2, links.Length);
        Assert.Contains(links, link => link.InnerText == "https://slimfaas.dev");
        Assert.Contains(links, link => link.InnerText == "SlimPlanet/SlimFaas");
        Assert.Contains(doc.MainDocumentPart.HyperlinkRelationships,
            relationship => relationship.Uri.ToString() == "https://slimfaas.dev/");
        Assert.Contains(doc.MainDocumentPart.HyperlinkRelationships,
            relationship => relationship.Uri.ToString() == "https://github.com/SlimPlanet/SlimFaas");
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("==========")]
    public void Write_DocxFile_WhenMarkdownContainsSeparator_CreatesParagraphBorder(string separator)
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("separator.docx", $"Intro\n\n{separator}\n\nOutro", null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);

        Assert.Contains(body!.Elements<Paragraph>(),
            paragraph => paragraph.ParagraphProperties?.ParagraphBorders?.BottomBorder is not null);
    }

    [Fact]
    public void Write_DocxFile_WhenMarkdownTableWithoutOuterPipesContainsBold_FormatsCellContent()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);
        var markdown = """
            Name | Value
            --- | ---
            Feature | **something**
            """;

        var writeResult = host.Write("table-no-outer-pipes.docx", markdown, null);

        Assert.True(writeResult.Success);
        using var doc = WordprocessingDocument.Open(writeResult.FilePath!, false);
        var document = doc.MainDocumentPart!.Document;
        Assert.NotNull(document);
        var body = document.Body;
        Assert.NotNull(body);

        var table = Assert.Single(body!.Elements<Table>());
        Assert.Contains(table.Descendants<Run>(), run =>
            run.InnerText == "something" && run.RunProperties?.Bold is not null);
        Assert.DoesNotContain("**", table.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_PdfFile_CreatesReadablePdf()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("plain.pdf", "Line 1\nLine 2", null);

        Assert.True(writeResult.Success);
        Assert.True(File.Exists(writeResult.FilePath));

        var readResult = host.Read("plain.pdf", "plain");
        Assert.True(readResult.Success);
        Assert.Contains("Line", readResult.Sections[0].Content);
    }

    [Fact]
    public void Write_PdfFile_WhenContentLooksLikeMarkdown_RendersReadableMarkdownContent()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);
        var markdown = """
            # Report title

            Sources : [https://slimfaas.dev](https://slimfaas.dev) - GitHub : [SlimPlanet/SlimFaas](https://github.com/SlimPlanet/SlimFaas)

            ---

            Name | Value
            --- | ---
            Feature | **something**
            """;

        var writeResult = host.Write("markdown.pdf", markdown, null);

        Assert.True(writeResult.Success);
        Assert.True(File.Exists(writeResult.FilePath));

        var readResult = host.Read("markdown.pdf", "plain");
        Assert.True(readResult.Success);
        var content = string.Join("\n", readResult.Sections.Select(section => section.Content));
        Assert.Contains("Report", content, StringComparison.Ordinal);
        Assert.Contains("https://slimfaas.dev", content, StringComparison.Ordinal);
        Assert.Contains("SlimPlanet/SlimFaas", content, StringComparison.Ordinal);
        Assert.Contains("something", content, StringComparison.Ordinal);
        Assert.DoesNotContain("**", content, StringComparison.Ordinal);
        Assert.DoesNotContain("[SlimPlanet/SlimFaas]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_XlsxFile_CreatesValidXlsx()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("test.xlsx", "Name\tAge\nAlice\t30\nBob\t25", null);

        Assert.True(writeResult.Success);
        Assert.True(File.Exists(writeResult.FilePath));

        // Read it back
        var readResult = host.Read("test.xlsx", "markdown");
        Assert.True(readResult.Success);
        Assert.NotEmpty(readResult.Sections);
        Assert.Contains("Alice", readResult.Sections[0].Content);
    }

    [Fact]
    public void Write_CreatesSubdirectories()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.Write("sub/dir/file.txt", "Nested!", null);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(root, "sub", "dir", "file.txt")));
    }

    [Fact]
    public void ListFiles_ReturnsAllowedFilesOnly()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "doc.txt"), "text");
        File.WriteAllText(Path.Combine(root, "data.csv"), "a,b");
        File.WriteAllText(Path.Combine(root, "binary.exe"), "nope");
        var host = CreateHost(root);

        var result = host.ListFiles(null, false);

        Assert.True(result.Success);
        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, f => Assert.True(f.Extension is ".txt" or ".csv"));
    }

    [Fact]
    public void ListFiles_Recursive_FindsNestedFiles()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "top.md"), "# Top");
        File.WriteAllText(Path.Combine(root, "sub", "nested.json"), "{}");
        var host = CreateHost(root);

        var result = host.ListFiles(null, true);

        Assert.True(result.Success);
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public void ListFiles_NonExistentDirectory_ReturnsError()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.ListFiles("nonexistent", false);

        Assert.False(result.Success);
        Assert.Equal("DIRECTORY_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Read_FileTooLarge_ReturnsError()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "big.txt");
        File.WriteAllText(file, new string('x', 200));
        var host = CreateHost(root, maxFileSize: 100);

        var result = host.Read("big.txt", null);

        Assert.False(result.Success);
        Assert.Equal("FILE_TOO_LARGE", result.ErrorCode);
    }

    [Fact]
    public void GetPolicy_ReturnsValidInfo()
    {
        var host = CreateHost();
        var info = host.GetPolicy();

        Assert.NotNull(info.DefaultWorkingDirectory);
        Assert.NotEmpty(info.AllowedExtensions);
        Assert.True(info.MaxFileSizeBytes > 0);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static DocumentOperationHost CreateHost(string? root = null, long maxFileSize = 50 * 1024 * 1024)
    {
        root ??= CreateTempDir();
        var settings = new DocumentServerSettings
        {
            DefaultWorkingDirectory = root,
            AllowedWorkingRoots = [root],
            MaxFileSizeBytes = maxFileSize
        };
        var policy = new DocumentPolicy(settings, root);
        return new DocumentOperationHost(policy, NullLogger<DocumentOperationHost>.Instance);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Document.Mcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
