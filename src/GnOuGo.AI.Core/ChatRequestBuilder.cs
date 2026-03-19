using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core;

/// <summary>
/// AOT-friendly JSON request builders for chat completion APIs (OpenAI &amp; Ollama).
/// </summary>
public static class ChatRequestBuilder
{
    /// <summary>
    /// Builds an OpenAI-compatible chat completion request body (simple system+user).
    /// </summary>
    public static byte[] OpenAi(string model, string systemPrompt, string userMessage,
        int maxTokens = 4096, int temperature = 0)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteNumber("temperature", temperature);
            WriteTextMessages(w, systemPrompt, userMessage);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Builds an OpenAI-compatible chat completion request with full control over parameters.
    /// Supports tools, structured output, and temperature as double.
    /// </summary>
    public static byte[] OpenAiFull(
        string model,
        string prompt,
        double? temperature = null,
        IReadOnlyList<LLMToolDef>? tools = null,
        JsonNode? structuredOutputSchema = null,
        bool? structuredOutputStrict = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);

            if (temperature.HasValue)
                w.WriteNumber("temperature", temperature.Value);

            // Messages: single user message
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt);
            w.WriteEndObject();
            w.WriteEndArray();

            // Tools
            if (tools is { Count: > 0 })
            {
                w.WriteStartArray("tools");
                foreach (var tool in tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", tool.Name);
                    if (tool.Description != null)
                        w.WriteString("description", tool.Description);
                    if (tool.InputSchema != null)
                    {
                        w.WritePropertyName("parameters");
                        tool.InputSchema.WriteTo(w);
                    }
                    w.WriteEndObject(); // function
                    w.WriteEndObject(); // tool
                }
                w.WriteEndArray();
            }

            // Structured output (response_format)
            if (structuredOutputSchema != null)
            {
                // When strict mode is enabled, OpenAI requires "additionalProperties": false
                // on every object in the schema. Patch it automatically.
                var schema = structuredOutputStrict == true
                    ? PatchAdditionalProperties(structuredOutputSchema.DeepClone())
                    : structuredOutputSchema;

                w.WriteStartObject("response_format");
                w.WriteString("type", "json_schema");
                w.WriteStartObject("json_schema");
                w.WriteString("name", "output");
                if (structuredOutputStrict == true)
                    w.WriteBoolean("strict", true);
                w.WritePropertyName("schema");
                schema.WriteTo(w);
                w.WriteEndObject(); // json_schema
                w.WriteEndObject(); // response_format
            }

            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Builds an Ollama chat completion request body (stream: false).
    /// </summary>
    public static byte[] Ollama(string model, string systemPrompt, string userMessage)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", false);
            WriteTextMessages(w, systemPrompt, userMessage);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Builds an Ollama chat completion request with full control.
    /// </summary>
    public static byte[] OllamaFull(
        string model,
        string prompt,
        double? temperature = null,
        IReadOnlyList<LLMToolDef>? tools = null,
        bool jsonMode = false)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("stream", false);

            if (temperature.HasValue)
                w.WriteNumber("temperature", temperature.Value);

            if (jsonMode)
                w.WriteString("format", "json");

            // Messages: single user message
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt);
            w.WriteEndObject();
            w.WriteEndArray();

            // Tools
            if (tools is { Count: > 0 })
            {
                w.WriteStartArray("tools");
                foreach (var tool in tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", tool.Name);
                    if (tool.Description != null)
                        w.WriteString("description", tool.Description);
                    if (tool.InputSchema != null)
                    {
                        w.WritePropertyName("parameters");
                        tool.InputSchema.WriteTo(w);
                    }
                    w.WriteEndObject(); // function
                    w.WriteEndObject(); // tool
                }
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Writes a messages array with system + user text messages.</summary>
    private static void WriteTextMessages(Utf8JsonWriter w, string systemPrompt, string userMessage)
    {
        w.WriteStartArray("messages");

        w.WriteStartObject();
        w.WriteString("role", "system");
        w.WriteString("content", systemPrompt);
        w.WriteEndObject();

        w.WriteStartObject();
        w.WriteString("role", "user");
        w.WriteString("content", userMessage);
        w.WriteEndObject();

        w.WriteEndArray();
    }

    /// <summary>
    /// Recursively patches a JSON Schema node to add <c>"additionalProperties": false</c>
    /// on every object definition. Required by OpenAI when <c>strict: true</c>.
    /// </summary>
    private static JsonNode PatchAdditionalProperties(JsonNode schema)
    {
        if (schema is not JsonObject obj) return schema;

        // If this node describes an object type, inject additionalProperties: false
        var typeVal = obj["type"]?.GetValue<string>();
        if (typeVal == "object" && !obj.ContainsKey("additionalProperties"))
        {
            obj["additionalProperties"] = false;
        }

        // Recurse into properties
        if (obj["properties"] is JsonObject props)
        {
            foreach (var kv in props)
            {
                if (kv.Value is JsonObject propObj)
                    PatchAdditionalProperties(propObj);
            }
        }

        // Recurse into items (arrays)
        if (obj["items"] is JsonObject items)
            PatchAdditionalProperties(items);

        // Recurse into anyOf / oneOf / allOf
        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (obj[keyword] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject itemObj)
                        PatchAdditionalProperties(itemObj);
                }
            }
        }

        return obj;
    }
}

/// <summary>
/// Tool definition for LLM function calling requests.
/// </summary>
public sealed class LLMToolDef
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonNode? InputSchema { get; set; }
}
