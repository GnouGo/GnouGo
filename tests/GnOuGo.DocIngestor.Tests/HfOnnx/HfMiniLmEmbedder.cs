using System.Net.Http.Headers;
using DocIngestor.Core.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace DocIngestor.Tests.HfOnnx;

internal sealed class HfMiniLmEmbedder : IEmbeddingModel, IDisposable
{
    public string Name => "hf-xenova-all-minilm-l6-v2-int8";
    public int Dimensions => 384;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _maxLen;

    public HfMiniLmEmbedder(string modelPath, string vocabPath, int maxLen = 256)
    {
        _session = new InferenceSession(modelPath);
        _tokenizer = BertTokenizer.Create(vocabPath);
        _maxLen = maxLen;
    }

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // encode ids (+ special tokens)
        var ids = _tokenizer.EncodeToIds(text ?? "", maxTokenCount: _maxLen, addSpecialTokens: true,
            out _, out _, considerPreTokenization: true, considerNormalization: true);

        // build attention mask
        var inputIds = ids.Select(i => (long)i).ToArray();
        var mask = Enumerable.Repeat(1L, inputIds.Length).ToArray();

        // pad
        if (inputIds.Length < _maxLen)
        {
            var pad = _tokenizer.PaddingTokenId;
            inputIds = inputIds.Concat(Enumerable.Repeat((long)pad, _maxLen - inputIds.Length)).ToArray();
            mask = mask.Concat(Enumerable.Repeat(0L, _maxLen - mask.Length)).ToArray();
        }
        else if (inputIds.Length > _maxLen)
        {
            inputIds = inputIds.Take(_maxLen).ToArray();
            mask = mask.Take(_maxLen).ToArray();
        }

        // create tensors [1, maxLen]
        var idsTensor = new DenseTensor<long>(new[] { 1, _maxLen });
        var maskTensor = new DenseTensor<long>(new[] { 1, _maxLen });

        for (int i = 0; i < _maxLen; i++)
        {
            idsTensor[0, i] = inputIds[i];
            maskTensor[0, i] = mask[i];
        }

        // feed inputs depending on model signature (some include token_type_ids)
        var inputs = new List<NamedOnnxValue>();
        try
        {
            var inputNames = _session.InputMetadata.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (inputNames.Contains("input_ids"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", idsTensor));
            else
                inputs.Add(NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), idsTensor));

            if (inputNames.Contains("attention_mask"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor));

            if (inputNames.Contains("token_type_ids"))
            {
                var tti = new DenseTensor<long>(new[] { 1, _maxLen });
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tti));
            }

            using var results = _session.Run(inputs);

            // Find best output:
            // - if model already includes pooling/normalize: expect [1,384] or [384]
            // - else: last_hidden_state [1,seq,hidden] -> mean pool -> normalize
            var first = results.First();

            if (first.Value is DenseTensor<float> t)
            {
                if (t.Rank == 2 && t.Dimensions[0] == 1 && t.Dimensions[1] == Dimensions)
                {
                    var vec = t.ToArray();
                    return ValueTask.FromResult(vec);
                }
                if (t.Rank == 1 && t.Dimensions[0] == Dimensions)
                {
                    return ValueTask.FromResult(t.ToArray());
                }
                if (t.Rank == 3 && t.Dimensions[0] == 1)
                {
                    // mean pooling with mask
                    int seq = t.Dimensions[1];
                    int hid = t.Dimensions[2];

                    var outVec = new float[hid];
                    double denom = 0;

                    for (int j = 0; j < seq; j++)
                    {
                        var m = maskTensor[0, j];
                        if (m == 0) continue;
                        denom += 1;
                        for (int k = 0; k < hid; k++)
                            outVec[k] += t[0, j, k];
                    }

                    if (denom > 0)
                    {
                        for (int k = 0; k < outVec.Length; k++)
                            outVec[k] = (float)(outVec[k] / denom);
                    }

                    NormalizeL2(outVec);
                    // Some models output 384 hidden; if not, we accept whatever (but test expects 384)
                    return ValueTask.FromResult(outVec);
                }
            }

            throw new InvalidOperationException("Unexpected ONNX output tensor type.");
        }
        finally
        {
                // NamedOnnxValue does not implement IDisposable in some OnnxRuntime versions; let GC handle it.
        }
    }

    public void Dispose()
        => _session.Dispose();

    private static void NormalizeL2(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++)
            sum += (double)v[i] * v[i];
        var norm = Math.Sqrt(sum);
        if (norm < 1e-12) return;
        for (int i = 0; i < v.Length; i++)
            v[i] = (float)(v[i] / norm);
    }
}

internal static class HfDownloader
{
    // Xenova repo contains onnx + vocab.txt (apache-2.0)
    private const string Base = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main";

    public static async Task<(string modelPath, string vocabPath)> EnsureAsync(CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cache = Path.Combine(home, ".cache", "docingestor", "hf", "Xenova", "all-MiniLM-L6-v2");
        Directory.CreateDirectory(cache);

        var modelPath = Path.Combine(cache, "onnx", "model_int8.onnx");
        var vocabPath = Path.Combine(cache, "vocab.txt");

        await DownloadIfMissing(modelPath, $"{Base}/onnx/model_int8.onnx", ct);
        await DownloadIfMissing(vocabPath, $"{Base}/vocab.txt", ct);

        return (modelPath, vocabPath);
    }

    private static async Task DownloadIfMissing(string destPath, string url, CancellationToken ct)
    {
        if (File.Exists(destPath) && new FileInfo(destPath).Length > 0) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DocIngestorTests", "1.0"));

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
    }
}
