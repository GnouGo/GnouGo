using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GnOuGo.Agent.Shared;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ChatMessageDto>))]
[JsonSerializable(typeof(ChatStreamRequestDto))]
[JsonSerializable(typeof(ChatStoreDto))]
[JsonSerializable(typeof(ChatSessionDto))]
[JsonSerializable(typeof(List<ChatSessionDto>))]
public partial class ChatJsonContext : JsonSerializerContext
{
}
