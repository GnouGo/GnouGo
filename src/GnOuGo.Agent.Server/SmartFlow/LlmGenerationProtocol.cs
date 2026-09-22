using System.Text.Json.Nodes;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

internal static class LlmGenerationProtocol
{
    public const string BackgroundLabel = "Background — Responses API";
    public const string ChatLabel = "Chat Completions — foreground";

    public static string Label(LLMBackgroundProtocolMode protocol)
        => protocol == LLMBackgroundProtocolMode.ChatCompletions ? ChatLabel : BackgroundLabel;

    public static LLMBackgroundProtocolMode? Read(JsonObject config)
    {
        var matches = config.Where(p => string.Equals(p.Key, "backgroundProtocol", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].Value is null)
            throw new InvalidOperationException("Invalid provider generation protocol.");
        return Parse(matches[0].Value);
    }

    public static LLMBackgroundProtocolMode? Parse(JsonNode? value)
    {
        if (value is null) return null;
        if (value is JsonValue json && json.TryGetValue<string>(out var text)
            && Enum.GetNames<LLMBackgroundProtocolMode>().Contains(text, StringComparer.OrdinalIgnoreCase)
            && Enum.TryParse<LLMBackgroundProtocolMode>(text, true, out var protocol))
            return protocol;
        throw new InvalidOperationException("Invalid provider generation protocol.");
    }
}
