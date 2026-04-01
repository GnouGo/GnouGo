using System.Collections.Generic;

namespace GnOuGo.Agent.Shared;

public sealed record ChatMessageDto(string Role, string Content);

public sealed record ChatStreamRequestDto(IReadOnlyList<ChatMessageDto> Messages);

// Browser-side persisted store (localStorage)
public sealed record ChatSessionDto(
    string Id,
    string Title,
    long UpdatedAtUnixMs,
    List<ChatMessageDto> Messages,
    string? AgentName = null);

public sealed record ChatStoreDto(
    string? ActiveId,
    List<ChatSessionDto> Sessions);

public sealed record LlmConfiguredProviderDto(
    string Key,
    string ProviderType,
    string Url,
    string? DefaultModel,
    bool IsDefault);

public sealed record LlmModelDto(
    string Id,
    string DisplayName,
    string ProviderType,
    string? OwnedBy = null);

public sealed record LlmProviderModelsDto(
    string Provider,
    string ProviderType,
    IReadOnlyList<LlmModelDto> Models);

