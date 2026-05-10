using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Images;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocIngestor.Core.Images;

public sealed class PptxImageExtractor : IImageExtractor
{
    private readonly ILogger<PptxImageExtractor> _logger;

    public PptxImageExtractor(ILogger<PptxImageExtractor>? logger = null)
    {
        _logger = logger ?? NullLogger<PptxImageExtractor>.Instance;
    }

    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.presentationml.presentation", StringComparison.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<ImageArtifact>> ExtractImagesAsync(
        DocumentSource source,
        ImageExtractionOptions options,
        CancellationToken ct = default)
    {
        if (!options.EnableImageDiscovery)
            return ValueTask.FromResult<IReadOnlyList<ImageArtifact>>(Array.Empty<ImageArtifact>());

        return new ValueTask<IReadOnlyList<ImageArtifact>>(Task.Run(() =>
        {
            source.Rewind();
            using var pres = PresentationDocument.Open(source.Content, false);
            var presPart = pres.PresentationPart;

            if (presPart?.Presentation is null)
                return (IReadOnlyList<ImageArtifact>)Array.Empty<ImageArtifact>();

            var slideIds =
                presPart.Presentation.SlideIdList?
                    .Elements<SlideId>()
                    .ToList()
                ?? new List<SlideId>();

            var artifacts = new List<ImageArtifact>();
            int globalIndex = 0;

            int slideNo = 1;
            foreach (var slideId in slideIds)
            {
                ct.ThrowIfCancellationRequested();

                var relId = slideId.RelationshipId;
                if (string.IsNullOrWhiteSpace(relId))
                {
                    slideNo++;
                    continue;
                }

                var slidePart = (SlidePart)presPart.GetPartById(relId);
                string sectionId = $"pptx:{slideNo}";

                // IMPORTANT:
                // SlidePart n'a PAS de SlideMasterPart direct.
                // Le master s'obtient via SlideLayoutPart.SlideMasterPart (ou fallback global).
                var masterPart =
                    slidePart.SlideLayoutPart?.SlideMasterPart
                    ?? presPart.SlideMasterParts.FirstOrDefault();

                // Collect images from:
                // - slide itself
                // - layout
                // - master
                // - notes (if any)
                var parts = new List<(string Kind, IEnumerable<ImagePart> Parts)>
                {
                    ("slide", slidePart.ImageParts),
                };

                if (slidePart.SlideLayoutPart is not null)
                    parts.Add(("layout", slidePart.SlideLayoutPart.ImageParts));

                if (masterPart is not null)
                    parts.Add(("master", masterPart.ImageParts));

                if (slidePart.NotesSlidePart is not null)
                    parts.Add(("notes", slidePart.NotesSlidePart.ImageParts));

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int perSlide = 0;

                foreach (var (kind, imgParts) in parts)
                {
                    foreach (var imgPart in imgParts)
                    {
                        if (perSlide >= options.MaxImagesPerSection) break;

                        ct.ThrowIfCancellationRequested();

                        var key = imgPart.Uri?.ToString() ?? $"{kind}:{globalIndex}";
                        if (!seen.Add(key)) continue;

                        var ctType = imgPart.ContentType ?? "application/octet-stream";
                        long? len = null;
                        byte[]? bytes = null;

                        using (var s = imgPart.GetStream())
                        {
                            try { len = s.Length; }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to read PPTX image stream length for '{FileName}' ({PartUri}).", source.FileName, imgPart.Uri);
                            }

                            if (len is not null &&
                                len.Value > options.MaxImageBytes &&
                                !(options.EnableOcr && len.Value <= 15_000_000))
                            {
                                continue;
                            }

                            if (options.LoadImageBytes)
                            {
                                using var ms = new MemoryStream();
                                s.CopyTo(ms);
                                bytes = ms.ToArray();
                            }
                        }

                        var (w, h) = bytes is null ? (null, null) : ImageHeaderParser.TryGetSize(ctType, bytes);

                        artifacts.Add(new ImageArtifact(
                            Id: $"pptx:slide{slideNo}:img{perSlide}",
                            PageNumber: slideNo,
                            SectionId: sectionId,
                            Name: null,
                            ContentType: ctType,
                            Width: w,
                            Height: h,
                            LengthBytes: len,
                            Bytes: bytes,
                            Metadata: new Dictionary<string, string>
                            {
                                ["slide"] = slideNo.ToString(),
                                ["source"] = "openxml",
                                ["partKind"] = kind,
                                ["partUri"] = imgPart.Uri?.ToString() ?? ""
                            }
                        ));

                        perSlide++;
                        globalIndex++;
                    }

                    if (perSlide >= options.MaxImagesPerSection)
                        break;
                }

                slideNo++;
            }

            return (IReadOnlyList<ImageArtifact>)artifacts;
        }, ct));
    }
}

public sealed class DocxImageExtractor : IImageExtractor
{
    private readonly ILogger<DocxImageExtractor> _logger;

    public DocxImageExtractor(ILogger<DocxImageExtractor>? logger = null)
    {
        _logger = logger ?? NullLogger<DocxImageExtractor>.Instance;
    }

    public bool CanHandle(string fileName, string? contentType = null)
        => fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
           || string.Equals(contentType, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<ImageArtifact>> ExtractImagesAsync(
        DocumentSource source,
        ImageExtractionOptions options,
        CancellationToken ct = default)
    {
        if (!options.EnableImageDiscovery)
            return ValueTask.FromResult<IReadOnlyList<ImageArtifact>>(Array.Empty<ImageArtifact>());

        return new ValueTask<IReadOnlyList<ImageArtifact>>(Task.Run(() =>
        {
            source.Rewind();
            using var doc = WordprocessingDocument.Open(source.Content, false);

            var artifacts = new List<ImageArtifact>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;

            void AddParts(string sectionId, IEnumerable<ImagePart> parts)
            {
                foreach (var imgPart in parts)
                {
                    if (count >= options.MaxImagesPerSection) return;

                    ct.ThrowIfCancellationRequested();

                    var key = imgPart.Uri?.ToString() ?? $"img:{count}";
                    if (!seen.Add(key)) continue;

                    var ctType = imgPart.ContentType ?? "application/octet-stream";
                    long? len = null;
                    byte[]? bytes = null;

                    using (var s = imgPart.GetStream())
                    {
                        try { len = s.Length; }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to read DOCX image stream length for '{FileName}' ({PartUri}).", source.FileName, imgPart.Uri);
                        }

                        if (len is not null &&
                            len.Value > options.MaxImageBytes &&
                            !(options.EnableOcr && len.Value <= 15_000_000))
                        {
                            continue;
                        }

                        if (options.LoadImageBytes)
                        {
                            using var ms = new MemoryStream();
                            s.CopyTo(ms);
                            bytes = ms.ToArray();
                        }
                    }

                    var (w, h) = bytes is null ? (null, null) : ImageHeaderParser.TryGetSize(ctType, bytes);

                    artifacts.Add(new ImageArtifact(
                        // FIX: sectionId contient déjà "docx:..."
                        Id: $"{sectionId}:img{count}",
                        PageNumber: null, // mapping image → page is non-trivial in DOCX
                        SectionId: sectionId,
                        Name: null,
                        ContentType: ctType,
                        Width: w,
                        Height: h,
                        LengthBytes: len,
                        Bytes: bytes,
                        Metadata: new Dictionary<string, string>
                        {
                            ["source"] = "openxml",
                            ["partUri"] = imgPart.Uri?.ToString() ?? ""
                        }
                    ));

                    count++;
                }
            }

            // Main document
            AddParts("docx:main", doc.MainDocumentPart?.ImageParts ?? Enumerable.Empty<ImagePart>());

            // Headers / Footers
            if (doc.MainDocumentPart is not null)
            {
                int h = 0;
                foreach (var hp in doc.MainDocumentPart.HeaderParts)
                {
                    AddParts($"docx:header{h}", hp.ImageParts);
                    h++;
                    if (count >= options.MaxImagesPerSection) break;
                }

                int f = 0;
                foreach (var fp in doc.MainDocumentPart.FooterParts)
                {
                    AddParts($"docx:footer{f}", fp.ImageParts);
                    f++;
                    if (count >= options.MaxImagesPerSection) break;
                }

                // Footnotes / Endnotes
                AddParts("docx:footnotes", doc.MainDocumentPart.FootnotesPart?.ImageParts ?? Enumerable.Empty<ImagePart>());
                AddParts("docx:endnotes", doc.MainDocumentPart.EndnotesPart?.ImageParts ?? Enumerable.Empty<ImagePart>());
            }

            return (IReadOnlyList<ImageArtifact>)artifacts;
        }, ct));
    }
}
