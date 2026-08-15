using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GnOuGo.AI.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Local;

/// <summary>Downloads and verifies allowlisted GGUF assets inside the workspace model directory.</summary>
public sealed class LocalModelManager : ILocalModelManager
{
    private static readonly ActivitySource ActivitySource = new("GnOuGo.AI.Local.Models");
    private static readonly Meter Meter = new("GnOuGo.AI.Local.Models");
    private static readonly Counter<long> DownloadedBytes = Meter.CreateCounter<long>("gnougo.local_model.downloaded_bytes");
    private static readonly Histogram<double> DownloadDuration = Meter.CreateHistogram<double>(
        "gnougo.local_model.download.duration",
        "s");

    private readonly HttpClient _http;
    private readonly string _modelsDirectory;
    private readonly Func<string, CancellationToken, Task>? _beforeRemove;
    private readonly ILogger<LocalModelManager> _logger;
    private readonly IReadOnlyList<LocalModelCatalogEntry> _catalog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalModelManager(
        HttpClient http,
        string modelsDirectory,
        Func<string, CancellationToken, Task>? beforeRemove = null,
        ILogger<LocalModelManager>? logger = null)
        : this(http, modelsDirectory, LocalModelCatalog.Entries, beforeRemove, logger)
    {
    }

    internal LocalModelManager(
        HttpClient http,
        string modelsDirectory,
        IReadOnlyList<LocalModelCatalogEntry> catalog,
        Func<string, CancellationToken, Task>? beforeRemove = null,
        ILogger<LocalModelManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(modelsDirectory))
            throw new ArgumentException("A model directory is required.", nameof(modelsDirectory));

        _http = http;
        _modelsDirectory = Path.GetFullPath(modelsDirectory);
        _catalog = catalog.Count == 0
            ? throw new ArgumentException("At least one allowlisted model is required.", nameof(catalog))
            : catalog;
        _beforeRemove = beforeRemove;
        _logger = logger ?? NullLogger<LocalModelManager>.Instance;
    }

    public string ModelsDirectory => _modelsDirectory;

    public async Task<LocalModelInfo> InstallAsync(
        string modelId,
        IProgress<LocalModelProgress>? progress = null,
        CancellationToken ct = default)
    {
        var entry = ResolveEntry(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        long downloadStartedAt = 0;
        try
        {
            Directory.CreateDirectory(_modelsDirectory);
            var finalPath = LocalModelCatalog.ResolveModelPath(_modelsDirectory, entry);
            var partialPath = finalPath + ".partial";

            if (await IsVerifiedAsync(finalPath, entry, ct).ConfigureAwait(false))
                return ToInfo(entry, LocalModelStatus.Installed, entry.SizeBytes);

            if (File.Exists(finalPath))
                File.Delete(finalPath);

            var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (existingBytes > entry.SizeBytes)
            {
                File.Delete(partialPath);
                existingBytes = 0;
            }

            using var activity = ActivitySource.StartActivity("local_model.install");
            downloadStartedAt = Stopwatch.GetTimestamp();
            activity?.SetTag("gen_ai.request.model", entry.Id);
            activity?.SetTag("gnougo.local_model.resume_bytes", existingBytes);

            using var request = new HttpRequestMessage(HttpMethod.Get, entry.DownloadUri);
            if (existingBytes > 0)
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);

            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (existingBytes > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                existingBytes = 0;
                File.Delete(partialPath);
            }
            response.EnsureSuccessStatusCode();

            var total = existingBytes;
            {
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var destination = new FileStream(
                    partialPath,
                    existingBytes == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);

                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    DownloadedBytes.Add(read, new KeyValuePair<string, object?>("model", entry.Id));
                    progress?.Report(new LocalModelProgress(entry.Id, total, entry.SizeBytes, total * 100d / entry.SizeBytes));
                }
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }

            if (!await IsVerifiedAsync(partialPath, entry, ct).ConfigureAwait(false))
            {
                File.Delete(partialPath);
                throw new LocalLLMException(
                    LocalLLMFailureKind.ModelUnavailable,
                    "The downloaded local model failed size or checksum verification.");
            }

            File.Move(partialPath, finalPath, overwrite: true);
            _logger.LogInformation("Installed local model {ModelId} ({SizeBytes} bytes).", entry.Id, entry.SizeBytes);
            progress?.Report(new LocalModelProgress(entry.Id, entry.SizeBytes, entry.SizeBytes, 100));
            return ToInfo(entry, LocalModelStatus.Installed, entry.SizeBytes);
        }
        finally
        {
            if (downloadStartedAt != 0)
            {
                DownloadDuration.Record(
                    Stopwatch.GetElapsedTime(downloadStartedAt).TotalSeconds,
                    new KeyValuePair<string, object?>("model", entry.Id));
            }
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalModelInfo>> ListAsync(CancellationToken ct = default)
    {
        var results = new List<LocalModelInfo>(_catalog.Count);
        foreach (var entry in _catalog)
        {
            ct.ThrowIfCancellationRequested();
            var finalPath = LocalModelCatalog.ResolveModelPath(_modelsDirectory, entry);
            var partialPath = finalPath + ".partial";
            if (File.Exists(finalPath))
            {
                var size = new FileInfo(finalPath).Length;
                var status = await IsVerifiedAsync(finalPath, entry, ct).ConfigureAwait(false)
                    ? LocalModelStatus.Installed
                    : LocalModelStatus.Corrupt;
                results.Add(ToInfo(entry, status, size));
            }
            else if (File.Exists(partialPath))
            {
                results.Add(ToInfo(entry, LocalModelStatus.Partial, new FileInfo(partialPath).Length));
            }
            else
            {
                results.Add(ToInfo(entry, LocalModelStatus.NotInstalled, 0));
            }
        }
        return results;
    }

    public async Task<bool> RemoveAsync(string modelId, CancellationToken ct = default)
    {
        var entry = ResolveEntry(modelId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_beforeRemove is not null)
                await _beforeRemove(entry.Id, ct).ConfigureAwait(false);

            var finalPath = LocalModelCatalog.ResolveModelPath(_modelsDirectory, entry);
            var partialPath = finalPath + ".partial";
            var removed = false;
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
                removed = true;
            }
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
                removed = true;
            }
            if (removed)
                _logger.LogInformation("Removed local model {ModelId}.", entry.Id);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal string ResolveInstalledPath(string modelId)
    {
        var entry = ResolveEntry(modelId);
        var path = LocalModelCatalog.ResolveModelPath(_modelsDirectory, entry);
        if (!File.Exists(path))
            throw new LocalLLMException(
                LocalLLMFailureKind.ModelUnavailable,
                $"Local model '{entry.Id}' is not installed. Run /models install {entry.Id}.");
        return path;
    }

    private static async Task<bool> IsVerifiedAsync(string path, LocalModelCatalogEntry entry, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != entry.SizeBytes)
            return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), entry.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private LocalModelCatalogEntry ResolveEntry(string modelId)
    {
        var entry = _catalog.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId?.Trim(), StringComparison.OrdinalIgnoreCase));
        return entry ?? throw new ArgumentException(
            $"Unknown local model '{modelId}'. Available models: {string.Join(", ", _catalog.Select(static candidate => candidate.Id))}.",
            nameof(modelId));
    }

    private static LocalModelInfo ToInfo(LocalModelCatalogEntry entry, LocalModelStatus status, long downloadedBytes)
        => new(
            entry.Id,
            entry.DisplayName,
            status,
            downloadedBytes,
            entry.SizeBytes,
            entry.License,
            entry.DownloadUri.ToString());
}
