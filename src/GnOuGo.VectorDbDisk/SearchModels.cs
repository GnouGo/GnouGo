namespace GnOuGo.VectorDbDisk;

public enum SearchMode
{
    VectorOnly,
    TextOnly,
    Hybrid
}

public sealed record SearchOptions(
    int TopK = 10,
    IMetadataFilter? Filter = null,
    SearchMode Mode = SearchMode.VectorOnly,
    double VectorWeight = 0.8,
    double TextWeight = 0.2,
    bool NormalizeQueryVector = true,
    int MaxCandidates = 50_000);

public sealed record SearchHit(
    string Id,
    string Text,
    IReadOnlyDictionary<string, string> Metadata,
    double Score,
    double? VectorScore = null,
    double? TextScore = null);

internal static class MathEx
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    public static float Norm(ReadOnlySpan<float> a)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * a[i];
        return MathF.Sqrt(sum);
    }

    public static void NormalizeInPlace(float[] v)
    {
        var n = Norm(v);
        if (n <= 0) return;
        for (int i = 0; i < v.Length; i++) v[i] /= n;
    }

    public static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);
}
