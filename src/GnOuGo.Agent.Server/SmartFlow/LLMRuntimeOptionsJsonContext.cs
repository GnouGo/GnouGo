using System.Text.Json.Serialization;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.SmartFlow;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LLMOptions))]
[JsonSerializable(typeof(ModelProviderOptions))]
[JsonSerializable(typeof(McpServerOptions))]
[JsonSerializable(typeof(Dictionary<string, ModelProviderOptions>))]
[JsonSerializable(typeof(Dictionary<string, McpServerOptions>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PersistedLlmSettings))]
internal partial class LlmRuntimeOptionsJsonContext : JsonSerializerContext
{
}

internal sealed class PersistedLlmSettings
{
    [JsonPropertyName("LLM")]
    public LLMOptions? Llm { get; set; }
}

