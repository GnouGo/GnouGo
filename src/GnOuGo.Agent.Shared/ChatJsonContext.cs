using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GnOuGo.Agent.Shared;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ChatMessageDto>))]
[JsonSerializable(typeof(ChatStreamRequestDto))]
[JsonSerializable(typeof(ChatCompletionResponseDto))]
[JsonSerializable(typeof(AppVersionDto))]
[JsonSerializable(typeof(ChatConversationSummaryDto))]
[JsonSerializable(typeof(List<ChatConversationSummaryDto>))]
[JsonSerializable(typeof(ChatStoreDto))]
[JsonSerializable(typeof(ChatSessionDto))]
[JsonSerializable(typeof(List<ChatSessionDto>))]
[JsonSerializable(typeof(LlmConfiguredProviderDto))]
[JsonSerializable(typeof(List<LlmConfiguredProviderDto>))]
[JsonSerializable(typeof(LlmModelDto))]
[JsonSerializable(typeof(List<LlmModelDto>))]
[JsonSerializable(typeof(LlmProviderModelsDto))]
public partial class ChatJsonContext : JsonSerializerContext
{
}
