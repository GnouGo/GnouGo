using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core;

/// <summary>
/// AOT-friendly JSON request builders for chat completion APIs (OpenAI &amp; Ollama).
/// </summary>
public static class ChatRequestBuilder
{
    private static readonly string[] IntegerSchemaKeywords =
    [
        "maxItems",
        "minItems",
        "maxLength",
        "minLength",
        "maxProperties",
        "minProperties"
    ];

    private static readonly string[] NumberSchemaKeywords =
    [
        "multipleOf",
        "minimum",
        "maximum",
        "exclusiveMinimum",
        "exclusiveMaximum"
    ];

    private static readonly string[] BooleanSchemaKeywords =
    [
        "additionalProperties",
        "uniqueItems"
    ];

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
        bool? structuredOutputStrict = null,
        string? reasoning = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);

            if (temperature.HasValue)
                w.WriteNumber("temperature", temperature.Value);

            // Reasoning effort (OpenAI o-series / gpt-5, GitHub Models, Anthropic via Copilot)
            var reasoningEffort = NormalizeOpenAiReasoning(reasoning);
            if (reasoningEffort != null)
                w.WriteString("reasoning_effort", reasoningEffort);

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
                var schema = NormalizeJsonSchemaForOpenAi(structuredOutputSchema.DeepClone());

                // When strict mode is enabled, OpenAI requires "additionalProperties": false
                // on every object in the schema. Patch it automatically.
                if (structuredOutputStrict == true)
                    schema = PatchAdditionalProperties(schema);

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
    /// Builds an OpenAI Responses API request body for provider-managed background execution.
    /// </summary>
    public static byte[] OpenAiResponsesBackground(
        string model,
        string prompt,
        double? temperature = null,
        string? reasoning = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteBoolean("background", true);

            if (temperature.HasValue)
                w.WriteNumber("temperature", temperature.Value);

            var reasoningEffort = NormalizeOpenAiReasoning(reasoning);
            if (reasoningEffort != null)
            {
                w.WriteStartObject("reasoning");
                w.WriteString("effort", reasoningEffort);
                w.WriteEndObject();
            }

            w.WriteStartArray("input");
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", prompt);
            w.WriteEndObject();
            w.WriteEndArray();

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
        bool jsonMode = false,
        string? reasoning = null)
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

            // Thinking toggle for reasoning-capable Ollama models (e.g. deepseek-r1, qwen3).
            var think = NormalizeOllamaThink(reasoning);
            if (think.HasValue)
                w.WriteBoolean("think", think.Value);

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

    /// <summary>
    /// Normalizes a generic reasoning level to the OpenAI <c>reasoning_effort</c> enum
    /// ("minimal" | "low" | "medium" | "high"). Returns <c>null</c> when the field
    /// must be omitted (auto / unknown / null).
    /// </summary>
    internal static string? NormalizeOpenAiReasoning(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => null,
            "minimal" or "min" => "minimal",
            "low" => "low",
            "medium" or "med" => "medium",
            "high" or "max" or "maximum" => "high",
            _ => null
        };
    }

    /// <summary>
    /// Normalizes a generic reasoning level to the Ollama <c>think</c> boolean.
    /// Returns <c>null</c> when the field must be omitted (auto / unknown / null).
    /// </summary>
    internal static bool? NormalizeOllamaThink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => null,
            "none" or "off" or "false" or "0" => false,
            "minimal" or "min" or "low" or "medium" or "med" or "high" or "max" or "maximum" or "true" or "1" => true,
            _ => null
        };
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
    /// Normalizes YAML-derived JSON Schema nodes for OpenAI. The workflow YAML parser treats
    /// the scalar value <c>null</c> as JSON null even when it was written as <c>"null"</c>,
    /// but JSON Schema requires <c>{ "type": "null" }</c> as a string literal.
    /// It also coerces quoted JSON Schema boolean/integer/number keywords emitted by YAML into
    /// their native JSON types. Real schema values such as <c>default</c>, <c>const</c>, or
    /// <c>enum</c> entries are left untouched.
    /// </summary>
    internal static JsonNode NormalizeJsonSchemaForOpenAi(JsonNode schema)
    {
        if (schema is not JsonObject obj) return schema;

        if (obj.ContainsKey("type"))
        {
            if (obj["type"] is null)
            {
                obj["type"] = "null";
            }
            else if (obj["type"] is JsonArray typeArray)
            {
                for (var i = 0; i < typeArray.Count; i++)
                {
                    if (typeArray[i] is null)
                        typeArray[i] = "null";
                }
            }
        }

        NormalizeOpenAiSchemaKeywordScalars(obj);

        if (obj["properties"] is JsonObject props)
        {
            foreach (var kv in props)
            {
                if (kv.Value is JsonObject propObj)
                    NormalizeJsonSchemaForOpenAi(propObj);
            }
        }

        if (obj["items"] is JsonObject items)
            NormalizeJsonSchemaForOpenAi(items);

        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (obj[keyword] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject itemObj)
                        NormalizeJsonSchemaForOpenAi(itemObj);
                }
            }
        }

        return obj;
    }

    private static void NormalizeOpenAiSchemaKeywordScalars(JsonObject obj)
    {
        foreach (var keyword in BooleanSchemaKeywords)
        {
            if (TryGetStringValue(obj[keyword], out var text) && bool.TryParse(text, out var value))
                obj[keyword] = value;
        }

        foreach (var keyword in IntegerSchemaKeywords)
        {
            if (TryGetStringValue(obj[keyword], out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                obj[keyword] = value;
        }

        foreach (var keyword in NumberSchemaKeywords)
        {
            if (TryGetStringValue(obj[keyword], out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                obj[keyword] = value;
        }
    }

    private static bool TryGetStringValue(JsonNode? node, out string text)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            text = raw.Trim();
            return true;
        }

        text = string.Empty;
        return false;
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
        if (typeVal == "object")
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
