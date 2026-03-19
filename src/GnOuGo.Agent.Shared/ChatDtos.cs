using System.Collections.Generic;

namespace GnOuGo.Agent.Shared;

public sealed record ChatMessageDto(string Role, string Content);

public sealed record ChatStreamRequestDto(IReadOnlyList<ChatMessageDto> Messages);

// Browser-side persisted store (localStorage)
public sealed record ChatSessionDto(
    string Id,
    string Title,
    long UpdatedAtUnixMs,
    List<ChatMessageDto> Messages);

public sealed record ChatStoreDto(
    string? ActiveId,
    List<ChatSessionDto> Sessions);
