using System.Net;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using HfTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace GnOuGo.VectorDbDisk.Tests;

internal sealed class HfMiniLmEmbedder : IAsyncDisposable
{
    private const string ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    private const string TokenizerUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/tokenizer.json";

    private readonly string _cacheDir;
    private readonly string _modelPath;
    private readonly string _tokenizerPath;

    private InferenceSession? _session;
    private HfTokenizer? _tokenizer;

    public HfMiniLmEmbedder(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
        _modelPath = Path.Combine(_cacheDir, "all-minilm-l6-v2.onnx");
        _tokenizerPath = Path.Combine(_cacheDir, "tokenizer.json");
    }

    public async Task<bool> EnsureReadyAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_modelPath))
                await DownloadAsync(ModelUrl, _modelPath, ct);

            if (!File.Exists(_tokenizerPath))
                await DownloadAsync(TokenizerUrl, _tokenizerPath, ct);

            _tokenizer = HfTokenizer.FromFile(_tokenizerPath);
            _session = new InferenceSession(_modelPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public float[] Embed(string text)
    {
        if (_session is null || _tokenizer is null)
            throw new InvalidOperationException("Not initialized. Call EnsureReadyAsync.");

        var encoding = _tokenizer.Encode(
            text,
            addSpecialTokens: true,
            includeTypeIds: true,
            includeAttentionMask: true
        ).First();

        var inputIdsArr = encoding.Ids.Select(i => (long)i).ToArray();
        var attentionArr = encoding.AttentionMask.Select(i => (long)i).ToArray();
        var typeArr = encoding.TypeIds.Select(i => (long)i).ToArray();

        int seqLen = inputIdsArr.Length;
        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attention = new DenseTensor<long>(new[] { 1, seqLen });
        var typeIds = new DenseTensor<long>(new[] { 1, seqLen });

        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = inputIdsArr[i];
            attention[0, i] = attentionArr[i];
            typeIds[0, i] = typeArr[i];
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
            NamedOnnxValue.CreateFromTensor("token_type_ids", typeIds),
        };

        using var results = _session.Run(inputs);

        var lastHidden = results.FirstOrDefault(r => string.Equals(r.Name, "last_hidden_state", StringComparison.OrdinalIgnoreCase))
                        ?? results.FirstOrDefault(r => r.AsTensor<float>().Rank == 3);

        var t = lastHidden!.AsTensor<float>(); // [1, seq, hidden]
        int hidden = t.Dimensions[2];

        var emb = new float[hidden];
        float count = 0;

        for (int i = 0; i < seqLen; i++)
        {
            if (attentionArr[i] == 0) continue;
            count++;
            for (int h = 0; h < hidden; h++)
                emb[h] += t[0, i, h];
        }

        if (count > 0)
            for (int h = 0; h < hidden; h++)
                emb[h] /= count;

        float norm = 0;
        for (int h = 0; h < hidden; h++) norm += emb[h] * emb[h];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
            for (int h = 0; h < hidden; h++) emb[h] /= norm;

        return emb;
    }

    private static async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await src.CopyToAsync(dst, ct);
    }

    public ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _session = null;
        _tokenizer?.Dispose();
        _tokenizer = null;
        return ValueTask.CompletedTask;
    }
}
