using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Images;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia;

namespace DocIngestor.Core.Extractors;

public sealed class PdfPigImageExtractor : IImageExtractor
{
    private const int RenderFallbackDpi = 200;

    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<ImageArtifact>> ExtractImagesAsync(DocumentSource source, ImageExtractionOptions options, CancellationToken ct = default)
    {
        return new ValueTask<IReadOnlyList<ImageArtifact>>(Task.Run(() =>
        {
            var results = new List<ImageArtifact>();

            // Open with Skia parsing options so we can render pages when needed.
            source.Rewind();
            using var pdf = PdfDocument.Open(source.Content, SkiaRenderingParsingOptions.Instance);
            pdf.AddSkiaPageFactory();

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                // 1) If the page text is "garbage" (broken glyph mapping), render whole page at 200 DPI.
                if (PdfPigQualityHeuristics.ShouldFallbackToRenderedPage(page))
                {
                    var id = $"pdf:{page.Number}:render:1";

                    byte[]? bytes = null;
                    if (options.LoadImageBytes)
                    {
                        bytes = RenderPagePng(pdf, page.Number, RenderFallbackDpi);

                        // Best effort max-size policy:
                        // - keep strict for normal discovery
                        // - allow larger payload when OCR is enabled (otherwise the fallback is useless)
                        if (bytes is not null)
                        {
                            var max = options.MaxImageBytes;
                            if (bytes.LongLength > max && !(options.EnableOcr && bytes.LongLength <= 15_000_000))
                            {
                                bytes = null; // keep metadata-only
                            }
                        }
                    }

                    var (w, h) = bytes is null ? (null, null) : ImageHeaderParser.TryGetSize("image/png", bytes);

                    var meta = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"] = "pdfpig.skia",
                        ["page"] = page.Number.ToString(),
                        ["kind"] = "rendered_page",
                        ["renderDpi"] = RenderFallbackDpi.ToString()
                    };

                    results.Add(new ImageArtifact(
                        Id: id,
                        PageNumber: page.Number,
                        SectionId: $"pdf:{page.Number}",
                        Name: $"page{page.Number}_render.png",
                        ContentType: "image/png",
                        Width: w,
                        Height: h,
                        LengthBytes: bytes?.LongLength,
                        Bytes: bytes,
                        Metadata: meta
                    ));
                }

                // 2) Embedded images (best effort)
                int idx = 0;
                foreach (var img in page.GetImages())
                {
                    ct.ThrowIfCancellationRequested();

                    if (idx >= options.MaxImagesPerSection) break;
                    idx++;

                    byte[]? bytes = null;
                    string? contentType = null;

                    // We can export as PNG for common image types.
                    if (img.TryGetPng(out var pngBytes))
                    {
                        contentType = "image/png";
                        if (options.LoadImageBytes)
                        {
                            if (pngBytes.LongLength <= options.MaxImageBytes || (options.EnableOcr && pngBytes.LongLength <= 15_000_000))
                                bytes = pngBytes;
                        }
                    }

                    var meta = new Dictionary<string, string>
                    {
                        ["page"] = page.Number.ToString(),
                        ["imageIndex"] = idx.ToString(),
                        ["bounds.left"] = img.Bounds.Left.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["bounds.bottom"] = img.Bounds.Bottom.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["bounds.right"] = img.Bounds.Right.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["bounds.top"] = img.Bounds.Top.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["widthInSamples"] = img.WidthInSamples.ToString(),
                        ["heightInSamples"] = img.HeightInSamples.ToString(),
                        ["source"] = "pdfpig"
                    };

                    results.Add(new ImageArtifact(
                        Id: $"pdf:{page.Number}:img:{idx}",
                        PageNumber: page.Number,
                        SectionId: $"pdf:{page.Number}",
                        Name: $"page{page.Number}_img{idx}",
                        ContentType: contentType,
                        Width: img.WidthInSamples,
                        Height: img.HeightInSamples,
                        LengthBytes: bytes?.LongLength,
                        Bytes: bytes,
                        Metadata: meta
                    ));
                }
            }

            return (IReadOnlyList<ImageArtifact>)results;
        }, ct));
    }

    private static byte[] RenderPagePng(PdfDocument pdf, int pageNumber, int dpi)
    {
        // Pdf uses 72 points per inch
        var scale = dpi / 72.0;
        using var ms = pdf.GetPageAsPng(pageNumber, scale : 2f,  quality: 90);
        return ms.ToArray();
    }
}
