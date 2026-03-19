using System.Text.Json;

namespace GnOuGo.AI.Core;

/// <summary>
/// AOT-friendly JSON request builders for vision chat APIs (OpenAI &amp; Ollama).
/// </summary>
public static class VisionRequestBuilder
{
    /// <summary>
    /// Builds an OpenAI-compatible vision chat request with an image data URL.
    /// </summary>
    public static byte[] OpenAi(string model, string systemPrompt, string imageDataUrl,
        string userText = "Extract all text from this image.", int maxTokens = 4096)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);

            w.WriteStartArray("messages");

            // System message
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", systemPrompt);
            w.WriteEndObject();

            // User message with image
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteStartArray("content");

            w.WriteStartObject();
            w.WriteString("type", "image_url");
            w.WriteStartObject("image_url");
            w.WriteString("url", imageDataUrl);
            w.WriteString("detail", "high");
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", userText);
            w.WriteEndObject();

            w.WriteEndArray(); // content
            w.WriteEndObject(); // user message

            w.WriteEndArray(); // messages
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Builds an Ollama vision chat request with a base64 image.
    /// </summary>
    public static byte[] Ollama(string model, string systemPrompt, string base64Image,
        string userText = "Extract all text from this image.")
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", false);

            w.WriteStartArray("messages");

            // System message
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", systemPrompt);
            w.WriteEndObject();

            // User message with image
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", userText);
            w.WriteStartArray("images");
            w.WriteStringValue(base64Image);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndArray(); // messages
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}

