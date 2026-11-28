using System.Text.Json;

namespace GnOuGo.AI.Core;

public sealed record LLMImageInput(string MimeType, byte[] Bytes);
internal enum VisionProtocol { Chat, Responses, Ollama, Anthropic }

/// <summary>
/// AOT-friendly JSON request builders for vision chat APIs (OpenAI &amp; Ollama).
/// </summary>
public static class VisionRequestBuilder
{
    internal static void WriteContent(Utf8JsonWriter w, string prompt, IReadOnlyList<LLMImageInput>? images, VisionProtocol protocol)
    {
        if (images is not { Count: > 0 } || protocol == VisionProtocol.Ollama)
        {
            w.WriteString("content", prompt);
            if (images is { Count: > 0 })
            {
                w.WriteStartArray("images");
                foreach (var image in images) w.WriteBase64StringValue(image.Bytes);
                w.WriteEndArray();
            }
            return;
        }
        w.WriteStartArray("content");
        w.WriteStartObject();
        w.WriteString("type", protocol == VisionProtocol.Responses ? "input_text" : "text");
        w.WriteString("text", prompt);
        w.WriteEndObject();
        foreach (var image in images)
        {
            w.WriteStartObject();
            if (protocol == VisionProtocol.Anthropic)
            {
                w.WriteString("type", "image");
                w.WriteStartObject("source");
                w.WriteString("type", "base64");
                w.WriteString("media_type", image.MimeType);
                w.WriteBase64String("data", image.Bytes);
                w.WriteEndObject();
            }
            else
            {
                var url = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}";
                if (protocol == VisionProtocol.Responses)
                {
                    w.WriteString("type", "input_image");
                    w.WriteString("image_url", url);
                }
                else WriteChatImage(w, url);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void WriteChatImage(Utf8JsonWriter w, string dataUrl)
    {
        w.WriteString("type", "image_url");
        w.WriteStartObject("image_url");
        w.WriteString("url", dataUrl);
        w.WriteString("detail", "high");
        w.WriteEndObject();
    }
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
            WriteChatImage(w, imageDataUrl);
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

