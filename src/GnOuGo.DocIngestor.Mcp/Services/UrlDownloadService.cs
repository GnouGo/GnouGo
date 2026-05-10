using System.Net.Http.Headers;
using System.Security.Cryptography;
using GnOuGo.DocIngestor.Mcp.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GnOuGo.DocIngestor.Mcp.Services;

public sealed class UrlDownloadService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DocsIngestorMcpOptions _options;
    private readonly ILogger<UrlDownloadService> _logger;

    public UrlDownloadService(
        IHttpClientFactory httpClientFactory,
        IOptions<DocsIngestorMcpOptions> options,
        ILogger<UrlDownloadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DownloadedDocument> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only absolute http/https URLs are supported.", nameof(url));

        var client = _httpClientFactory.CreateClient(nameof(UrlDownloadService));
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.DownloadTimeoutSeconds));

        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var fileName = ResolveFileName(uri, response.Content.Headers.ContentDisposition);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var tempPath = Path.Combine(Path.GetTempPath(), "gnougo-docs-ingestor", Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(tempPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            total += read;
            if (total > _options.MaxDownloadBytes)
                throw new InvalidOperationException($"Downloaded file exceeds the configured limit of {_options.MaxDownloadBytes} bytes.");

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new DownloadedDocument(url, tempPath, fileName, contentType, total, sha256)
        {
            Logger = _logger
        };
    }

    private static string ResolveFileName(Uri uri, ContentDispositionHeaderValue? contentDisposition)
    {
        var fromHeader = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fromHeader))
            return SanitizeFileName(fromHeader.Trim('"'));

        var lastSegment = uri.Segments.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(lastSegment) && lastSegment != "/")
            return SanitizeFileName(Uri.UnescapeDataString(lastSegment));

        return "document.bin";
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "document.bin" : sanitized;
    }
}

