using System.Text;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Metadata;
using DocIngestor.Core.Models;
using DocIngestor.Core.Formatting;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DocIngestor.Core.Extractors;

public sealed class PptxOpenXmlExtractor : IDocumentTextExtractor
{
    private readonly ILogger<PptxOpenXmlExtractor> _logger;

    public PptxOpenXmlExtractor(ILogger<PptxOpenXmlExtractor>? logger = null)
    {
        _logger = logger ?? NullLogger<PptxOpenXmlExtractor>.Instance;
    }

    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.presentationml.presentation", StringComparison.OrdinalIgnoreCase);

    public ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken ct = default)
    {
        return new ValueTask<ExtractedDocument>(Task.Run(() =>
        {
            var mime = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            var meta = MetadataDefaults.FromSource(source, mime);

            source.Rewind();
            using var pres = PresentationDocument.Open(source.Content, false);

            try
            {
                var p = pres.PackageProperties;
                if (!string.IsNullOrWhiteSpace(p.Title)) meta["title"] = p.Title!;
                if (!string.IsNullOrWhiteSpace(p.Creator)) meta["author"] = p.Creator!;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read PPTX package properties for '{FileName}'.", source.FileName);
            }

            var presPart = pres.PresentationPart;
            var slideIds = presPart?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList()
                          ?? new List<P.SlideId>();

            var sections = new List<ExtractedSection>(slideIds.Count);
            int slideNo = 1;

            foreach (var slideId in slideIds)
            {
                var relId = slideId.RelationshipId;
                if (relId is null) continue;

                var slidePart = (SlidePart)presPart!.GetPartById(relId);
                var lines = new List<string>();

// Prefer paragraph-level concatenation so we don't glue words across runs
foreach (var p in slidePart.Slide.Descendants<A.Paragraph>())
{
    var line = string.Join(" ", p.Descendants<A.Text>().Select(t => (t.Text ?? "").Trim()).Where(s => s.Length > 0));
    if (!string.IsNullOrWhiteSpace(line))
        lines.Add(line);
}

var plainText = TextNormalization.NormalizeWhitespaceForDisplay(string.Join("\n", lines));

var mdSb = new StringBuilder();
mdSb.Append("## Slide ").Append(slideNo).AppendLine();
mdSb.AppendLine();
foreach (var line in lines)
    mdSb.Append("- ").AppendLine(TextNormalization.NormalizeWhitespaceForDisplay(line));
var md = mdSb.ToString().TrimEnd();

sections.Add(new ExtractedSection(
    SectionId: $"pptx:{slideNo}",
    Title: $"slide {slideNo}",
    PageNumber: slideNo,
    Text: plainText,
    Metadata: new Dictionary<string, string>
    {
        ["slide"] = slideNo.ToString(),
        ["format"] = "markdown",
        ["source"] = "pptx"
    }
)
{ 
    Markdown = md
});
slideNo++;
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

    private static string MakeId(DocumentSource source)
        => $"{source.FileName}:{source.ComputeSha256().Substring(0, 12)}";
}
