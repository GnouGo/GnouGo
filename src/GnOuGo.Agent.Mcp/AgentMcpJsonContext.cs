using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp.Services;

namespace GnOuGo.Agent.Mcp;

internal static class AgentMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, AgentMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(AgentDto))]
[JsonSerializable(typeof(IReadOnlyList<AgentDto>))]
[JsonSerializable(typeof(AgentToolResult))]
[JsonSerializable(typeof(AgentListToolResult))]
[JsonSerializable(typeof(AgentDeleteToolResult))]
[JsonSerializable(typeof(ChatHistoryMessageInput))]
[JsonSerializable(typeof(List<ChatHistoryMessageInput>))]
[JsonSerializable(typeof(ChatHistoryMessageDto))]
[JsonSerializable(typeof(IReadOnlyList<ChatHistoryMessageDto>))]
[JsonSerializable(typeof(ChatHistoryAppendToolResult))]
[JsonSerializable(typeof(ChatHistoryGetToolResult))]
[JsonSerializable(typeof(UserConfigDto))]
[JsonSerializable(typeof(UserConfigToolResult))]
[JsonSerializable(typeof(Dictionary<string, LLMModelMetadata>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, LLMModelMetadata>))]
[JsonSerializable(typeof(LLMModelMetadata))]
[JsonSerializable(typeof(ModelPricingMetadata))]
[JsonSerializable(typeof(ModelCapabilityMetadata))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(UserConfigSnapshot))]
[JsonSerializable(typeof(UserConfigUpdate))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AgentMcpJsonContext : JsonSerializerContext;

