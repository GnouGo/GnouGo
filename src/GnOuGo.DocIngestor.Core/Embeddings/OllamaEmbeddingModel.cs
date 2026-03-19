using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.AI.Core;

namespace DocIngestor.Core.Embeddings;

/// <summary>
/// Embedding model that uses the Ollama local API.
///
/// <para><b>Ollama</b> runs open-source models locally (no cloud, no API key).
/// Popular embedding models include:</para>
/// <list type="bullet">
///   <item><c>nomic-embed-text</c> — 768 dims, excellent quality/speed ratio</item>
///   <item><c>mxbai-embed-large</c> — 1024 dims, high quality</item>
///   <item><c>all-minilm</c> — 384 dims, very fast, lightweight</item>
///   <item><c>snowflake-arctic-embed</c> — 1024 dims, top quality</item>
/// </list>
///
/// <para>Ollama API endpoint: <c>POST /api/embed</c></para>
/// <para>Request:  <c>{ "model": "...", "input": ["text1", "text2"] }</c></para>
/// <para>Response: <c>{ "model": "...", "embeddings": [[...], [...]] }</c></para>
/// </summary>
public sealed class OllamaEmbeddingModel : IEmbeddingModel
{
    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int Dimensions => _dims > 0 ? _dims : _defaultDims;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly int _defaultDims;
    private int _dims;

    /// <param name="name">Registry name (e.g. "ollama-nomic").</param>
    /// <param name="baseUrl">Ollama base URL (e.g. "http://localhost:11434").</param>
    /// <param name="model">Ollama model name (e.g. "nomic-embed-text").</param>
    /// <param name="http">Shared HttpClient instance.</param>
    /// <param name="defaultDims">Expected dimensions (used before the first call).</param>
    public OllamaEmbeddingModel(
        string name,
        string baseUrl,
        string model,
        HttpClient http,
        int defaultDims = 768)
    {
        Name = name;
        _baseUrl = (baseUrl ?? "http://localhost:11434").TrimEnd('/');
        _model = model ?? "nomic-embed-text";
        _http = http;
        _defaultDims = defaultDims;
    }

    /// <inheritdoc />
    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync(new[] { text }, ct);
        return results[0];
    }

    /// <summary>
    /// Embed multiple texts in a single Ollama API call.
    /// Ollama natively supports batch via the <c>input</c> array.
    /// </summary>
    public async ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return Array.Empty<float[]>();

        var url = OllamaEndpoints.Embed(_baseUrl);
        byte[] payload = EmbeddingRequestBuilder.Ollama(_model, texts);

        using var req = HttpRequestHelper.CreateJsonPost(url, payload);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
            throw new InvalidOperationException(
                $"Ollama embed call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = EmbeddingResponseParser.ParseOllama(json.RootElement);
        if (results.Length > 0)
            _dims = results[0].Length;

        return results;
    }
}

