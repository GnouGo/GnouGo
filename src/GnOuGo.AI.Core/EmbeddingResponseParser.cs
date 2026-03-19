using System.Text.Json;

namespace GnOuGo.AI.Core;

/// <summary>
/// Parses embedding responses from OpenAI and Ollama APIs.
/// </summary>
public static class EmbeddingResponseParser
{
    /// <summary>
    /// Parses OpenAI embedding response: data[].embedding[] (sorted by index).
    /// Returns vectors indexed by the "index" field.
    /// </summary>
    public static float[][] ParseOpenAi(JsonElement root, int expectedCount)
    {
        var data = root.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidOperationException("Embeddings response has no data[].");

        var results = new float[expectedCount][];
        foreach (var item in data.EnumerateArray())
        {
            var idx = item.GetProperty("index").GetInt32();
            results[idx] = ParseFloatArray(item.GetProperty("embedding"));
        }
        return results;
    }

    /// <summary>
    /// Parses a single OpenAI embedding response: data[0].embedding[]
    /// </summary>
    public static float[] ParseOpenAiSingle(JsonElement root)
    {
        var data = root.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidOperationException("Embeddings response has no data[].");

        var emb = data[0].GetProperty("embedding");
        if (emb.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Embeddings response data[0].embedding is not an array.");

        return ParseFloatArray(emb);
    }

    /// <summary>
    /// Parses Ollama embedding response: embeddings[][] 
    /// </summary>
    public static float[][] ParseOllama(JsonElement root)
    {
        var embeddings = root.GetProperty("embeddings");
        if (embeddings.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Ollama response missing 'embeddings' array.");

        var results = new float[embeddings.GetArrayLength()][];
        int idx = 0;
        foreach (var emb in embeddings.EnumerateArray())
            results[idx++] = ParseFloatArray(emb);

        return results;
    }

    /// <summary>Parses a JSON array of numbers into a float[].</summary>
    public static float[] ParseFloatArray(JsonElement array)
    {
        var vec = new float[array.GetArrayLength()];
        int i = 0;
        foreach (var n in array.EnumerateArray())
            vec[i++] = (float)n.GetDouble();
        return vec;
    }
}

