using DocIngestor.Tests.HfOnnx;
using Xunit;

namespace DocIngestor.Tests;

public sealed class OnnxEmbeddingIntegrationTests
{
    [Fact]
    public async Task Hf_MiniLm_Embeddings_Work()
    {
        if (Environment.GetEnvironmentVariable("RUN_HF_TESTS") != "1")
            return; // opt-in integration test (keeps default test runs green)

        var (modelPath, vocabPath) = await HfDownloader.EnsureAsync(CancellationToken.None);

        using var emb = new HfMiniLmEmbedder(modelPath, vocabPath, maxLen: 256);

        var v1 = await emb.EmbedAsync("Cats are wonderful pets.");
        var v2 = await emb.EmbedAsync("Kittens are small cats.");
        var v3 = await emb.EmbedAsync("The CPU executes instructions.");

        Assert.Equal(384, v1.Length);
        Assert.Equal(384, v2.Length);

        double sim12 = Cosine(v1, v2);
        double sim13 = Cosine(v1, v3);

        Assert.True(sim12 > sim13, $"Expected sim(cats,kittens) > sim(cats,cpu) but got {sim12:0.000} vs {sim13:0.000}");
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom <= 1e-12 ? 0 : dot / denom;
    }
}
