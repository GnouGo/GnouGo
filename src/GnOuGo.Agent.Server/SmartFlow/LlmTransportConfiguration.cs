using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

internal static class LlmTransportConfiguration
{
    public static LLMStreamingMode? ReadMode(JsonObject config) => ParseMode(Read(config, "streamingMode"));
    public static LLMStreamingMode? ParseMode(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text) &&
            Enum.GetNames<LLMStreamingMode>().Contains(text, StringComparer.OrdinalIgnoreCase) &&
            Enum.TryParse<LLMStreamingMode>(text, true, out var mode)) return mode;
        throw new InvalidOperationException("Invalid provider streaming mode.");
    }
    public static Dictionary<string, bool>? ReadSupport(JsonObject config)
    {
        var node = Read(config, "streamingSupport");
        if (node is null) return null;
        if (node is not JsonObject values) throw new InvalidOperationException("Invalid native streaming capability declarations.");
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var field in values)
        {
            if (field.Value is not JsonValue value || !value.TryGetValue<bool>(out var supported))
                throw new InvalidOperationException("Native streaming capability must be explicitly true or false.");
            result.Add(field.Key, supported);
        }
        LLMOptionsValidation.ValidateProvider("transport", new() { RequestPolicy = new() { StreamingSupport = result } });
        return result;
    }
    public static Dictionary<string, LLMModelTransportOptions>? ReadOverrides(JsonObject config)
    {
        var node = Read(config, "modelTransportOverrides");
        if (node is null) return null;
        if (node is not JsonObject rows) throw new InvalidOperationException("Model transport overrides must be an object.");
        var result = new Dictionary<string, LLMModelTransportOptions>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Value is not JsonObject value || value.Any(p => p.Key is not ("streamingMode" or "streamingSupport")))
                throw new InvalidOperationException("Invalid exact model transport override.");
            result.Add(row.Key, new() { StreamingMode = ReadMode(value), StreamingSupport = ReadSupport(value) });
        }
        LLMOptionsValidation.ValidateProvider("transport", new() { ModelTransportOverrides = result });
        return result;
    }
    public static void Apply(JsonObject config, ModelProviderOptions options)
    {
        if (Read(config, "streamingMode") is not null) options.RequestPolicy.StreamingMode = ReadMode(config);
        if (Read(config, "streamingSupport") is not null) options.RequestPolicy.StreamingSupport = ReadSupport(config);
        if (Read(config, "modelTransportOverrides") is not null) options.ModelTransportOverrides = ReadOverrides(config);
    }
    private static JsonNode? Read(JsonObject config, string name)
    {
        var matches = config.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].Value is null) throw new InvalidOperationException("Invalid or ambiguous provider transport configuration.");
        return matches[0].Value;
    }
    public static JsonNode? SupportNode(Dictionary<string, bool>? support)
    {
        if (support is null) return null;
        var node = new JsonObject(); foreach (var pair in support) node[pair.Key] = pair.Value; return node;
    }
    public static JsonNode? OverridesNode(Dictionary<string, LLMModelTransportOptions>? overrides)
        => overrides is null ? null : JsonSerializer.SerializeToNode(overrides, StreamingJsonContext.Default.DictionaryStringLLMModelTransportOptions);
}
