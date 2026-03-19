using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Extractors;
using DocIngestor.Core.Images;
using DocIngestor.Core.Pipeline;
using Xunit;

namespace DocIngestor.Tests;

public sealed class RouterTests
{
    [Fact]
    public void Router_Selects_Extractor()
    {
        var router = new DocumentRouter(new IDocumentTextExtractor[]
        {
            new PdfPigExtractor(),
            new DocxOpenXmlExtractor(),
            new PptxOpenXmlExtractor(),
            new XlsxOpenXmlExtractor(),
            new PlainTextExtractor(), // catch-all — must be last
        }, new IImageExtractor[]
        {
            new PptxImageExtractor(),
            new DocxImageExtractor(),
            new PdfPigImageExtractor(),
            new XlsxImageExtractor()
        });

        // Binary formats
        Assert.IsType<DocxOpenXmlExtractor>(router.GetTextExtractor("a.docx"));
        Assert.IsType<PdfPigExtractor>(router.GetTextExtractor("a.pdf"));
        Assert.IsType<PptxOpenXmlExtractor>(router.GetTextExtractor("slides.pptx"));
        Assert.IsType<XlsxOpenXmlExtractor>(router.GetTextExtractor("data.xlsx"));

        // Plain text (all handled by PlainTextExtractor)
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("a.md"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("readme.txt"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("app.cs"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("index.ts"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("main.py"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("config.json"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("config.yaml"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("script.sh"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("Makefile"));
        Assert.IsType<PlainTextExtractor>(router.GetTextExtractor("Dockerfile"));

        // Image extractors
        Assert.NotNull(router.TryGetImageExtractor("a.pptx"));
        Assert.NotNull(router.TryGetImageExtractor("a.pdf"));
        Assert.NotNull(router.TryGetImageExtractor("a.xlsx"));
    }
}
