using System.Text.Json;

namespace GnOuGo.AI.Core;

/// <summary>
/// AOT-friendly JSON request builders for embedding APIs (OpenAI &amp; Ollama).
/// </summary>
public static class EmbeddingRequestBuilder
{
    /// <summary>Builds an OpenAI-compatible single-text embedding request.</summary>
    public static byte[] OpenAi(string model, string input)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteString("input", input);
            w.WriteString("encoding_format", "float");
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Builds an OpenAI-compatible batch embedding request.</summary>
    public static byte[] OpenAiBatch(string model, IReadOnlyList<string> inputs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteStartArray("input");
            foreach (var input in inputs)
                w.WriteStringValue(input);
            w.WriteEndArray();
            w.WriteString("encoding_format", "float");
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Builds an Ollama embedding request (supports batch via input array).</summary>
    public static byte[] Ollama(string model, IReadOnlyList<string> inputs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteStartArray("input");
            foreach (var input in inputs)
                w.WriteStringValue(input);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}

