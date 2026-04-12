using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace GnOuGo.Document.Mcp;

/// <summary>
/// Generates a simple DOCX file from plain text (one paragraph per line).
/// </summary>
internal static class DocxWriter
{
    public static void WriteSimpleDocx(string path, string content)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
        var body = mainPart.Document.AppendChild(new Body());

        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var para = body.AppendChild(new Paragraph());
            var run = para.AppendChild(new Run());
            run.AppendChild(new Text(line.TrimEnd('\r')) { Space = SpaceProcessingModeValues.Preserve });
        }

        mainPart.Document.Save();
    }
}

