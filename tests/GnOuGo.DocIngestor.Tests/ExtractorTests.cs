using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Extractors;
using DocIngestor.Tests.TestAssets;
using Xunit;

namespace DocIngestor.Tests;

public sealed class ExtractorTests
{
    [Fact]
    public async Task DocxExtractor_Extracts_Text()
    {
        var tmp = CreateTempDir();
        var path = OfficeAssetFactory.CreateDocx(tmp);

        await using var source = OpenSource(path);
        var ex = new DocxOpenXmlExtractor();
        var doc = await ex.ExtractAsync(source);

        Assert.Single(doc.Sections);
        Assert.Contains("Hello DOCX", doc.Sections[0].Text);
        Assert.Contains("cats", doc.Sections[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PptxExtractor_Extracts_Slides()
    {
        var tmp = CreateTempDir();
        var path = OfficeAssetFactory.CreatePptx(tmp);

        await using var source = OpenSource(path);
        var ex = new PptxOpenXmlExtractor();
        var doc = await ex.ExtractAsync(source);

        Assert.True(doc.Sections.Count >= 2);
        Assert.Contains("Hello PPTX", doc.Sections[0].Text);
        Assert.Equal(1, doc.Sections[0].PageNumber);
    }

    [Fact]
    public async Task XlsxExtractor_Extracts_Sheets()
    {
        var tmp = CreateTempDir();
        var path = OfficeAssetFactory.CreateXlsx(tmp);

        await using var source = OpenSource(path);
        var ex = new XlsxOpenXmlExtractor();
        var doc = await ex.ExtractAsync(source);

        Assert.Single(doc.Sections);
        Assert.Contains("Sheet1", doc.Sections[0].Title);
        Assert.Contains("Hello", doc.Sections[0].Text);
    }

    [Fact]
    public async Task PlainTextExtractor_Extracts_Markdown()
    {
        var tmp = CreateTempDir();
        var path = Path.Combine(tmp, "a.md");
        await File.WriteAllTextAsync(path, "# Title\n\nHello markdown");

        await using var source = OpenSource(path);
        var ex = new PlainTextExtractor();
        var doc = await ex.ExtractAsync(source);

        Assert.Single(doc.Sections);
        Assert.Contains("Hello markdown", doc.Sections[0].Text);
        Assert.Equal("text/markdown", doc.MimeType);
        Assert.True(doc.Metadata.ContainsKey("sha256"));
    }

    [Fact]
    public async Task PlainTextExtractor_Extracts_CSharp()
    {
        var tmp = CreateTempDir();
        var path = Path.Combine(tmp, "Program.cs");
        await File.WriteAllTextAsync(path, "using System;\n\nclass Program\n{\n    static void Main() { }\n}");

        await using var source = OpenSource(path);
        var ex = new PlainTextExtractor();
        var doc = await ex.ExtractAsync(source);

        Assert.Single(doc.Sections);
        Assert.Contains("class Program", doc.Sections[0].Text);
        Assert.Equal("text/x-csharp", doc.MimeType);
        Assert.Equal("csharp", doc.Metadata["language"]);
    }

    [Fact]
    public async Task PlainTextExtractor_Extracts_Json()
    {
        var tmp = CreateTempDir();
        var path = Path.Combine(tmp, "config.json");
        await File.WriteAllTextAsync(path, "{\"key\": \"value\"}");

        await using var source = OpenSource(path);
        var ex = new PlainTextExtractor();
        var doc = await ex.ExtractAsync(source);

        Assert.Single(doc.Sections);
        Assert.Contains("\"key\"", doc.Sections[0].Text);
        Assert.Equal("application/json", doc.MimeType);
        Assert.Equal("json", doc.Metadata["language"]);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DocIngestorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Helper: open a file as a DocumentSource backed by a MemoryStream.</summary>
    private static DocumentSource OpenSource(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var ms = new MemoryStream(bytes);
        return new DocumentSource(ms, Path.GetFileName(filePath), ownsStream: true);
    }
}
