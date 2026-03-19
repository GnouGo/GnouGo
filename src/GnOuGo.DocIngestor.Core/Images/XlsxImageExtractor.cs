using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Images;
using DocumentFormat.OpenXml.Packaging;

namespace DocIngestor.Core.Images;

/// <summary>
/// Extracts embedded images from .xlsx files using OpenXML.
/// Note: Excel images are usually stored in DrawingsPart(s) referenced by worksheet parts.
/// This extractor collects ImageParts from every worksheet drawing.
/// </summary>
public sealed class XlsxImageExtractor : IImageExtractor
{
    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<ImageArtifact>> ExtractImagesAsync(DocumentSource source, ImageExtractionOptions options, CancellationToken ct = default)
    {
        if (!options.EnableImageDiscovery)
            return ValueTask.FromResult<IReadOnlyList<ImageArtifact>>(Array.Empty<ImageArtifact>());

        return new ValueTask<IReadOnlyList<ImageArtifact>>(Task.Run(() =>
        {
            var artifacts = new List<ImageArtifact>();
            int globalIndex = 0;

            source.Rewind();
            using var doc = SpreadsheetDocument.Open(source.Content, false);
            var wbPart = doc.WorkbookPart;
            if (wbPart is null)
                return (IReadOnlyList<ImageArtifact>)artifacts;

            // Map worksheet part -> sheet name (best effort)
            var sheetNameByRelId = new Dictionary<string, string>(StringComparer.Ordinal);
            var sheets = wbPart.Workbook?.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>();
            if (sheets is not null)
            {
                foreach (var s in sheets)
                {
                    if (s.Id?.Value is string rid && !string.IsNullOrWhiteSpace(s.Name?.Value))
                        sheetNameByRelId[rid] = s.Name!.Value!;
                }
            }

            foreach (var wsPart in wbPart.WorksheetParts)
            {
                ct.ThrowIfCancellationRequested();

                // Find sheet name
                string sectionId = "xlsx:sheet";
                try
                {
                    var relId = wbPart.GetIdOfPart(wsPart);
                    if (relId is not null && sheetNameByRelId.TryGetValue(relId, out var sn))
                        sectionId = $"xlsx:{sn}";
                    else if (relId is not null)
                        sectionId = $"xlsx:{relId}";
                }
                catch
                {
                    // ignore
                }

                var drawingsPart = wsPart.DrawingsPart;
                if (drawingsPart is null) continue;

                foreach (var imgPart in drawingsPart.ImageParts)
                {
                    if (artifacts.Count >= options.MaxImagesPerSection) break;

                    ct.ThrowIfCancellationRequested();

                    var ctType = imgPart.ContentType ?? "application/octet-stream";
                    long? len = null;
                    byte[]? bytes = null;

                    using (var s = imgPart.GetStream())
                    {
                        try { len = s.Length; } catch { /* ignore */ }

                        if (len is not null && len.Value > options.MaxImageBytes)
                            continue;

                        if (options.LoadImageBytes)
                        {
                            using var ms = new MemoryStream();
                            s.CopyTo(ms);
                            bytes = ms.ToArray();
                        }
                    }

                    var (w, h) = bytes is null ? (null, null) : ImageHeaderParser.TryGetSize(ctType, bytes);

                    var meta = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"] = "openxml",
                        ["docType"] = "xlsx",
                        ["sectionId"] = sectionId,
                    };

                    artifacts.Add(new ImageArtifact(
                        Id: $"xlsx:img{globalIndex}",
                        PageNumber: null,
                        SectionId: sectionId,
                        Name: null,
                        ContentType: ctType,
                        Width: w,
                        Height: h,
                        LengthBytes: len,
                        Bytes: bytes,
                        Metadata: meta
                    ));

                    globalIndex++;
                }

                if (artifacts.Count >= options.MaxImagesPerSection)
                    break;
            }

            return (IReadOnlyList<ImageArtifact>)artifacts;
        }, ct));
    }
}
