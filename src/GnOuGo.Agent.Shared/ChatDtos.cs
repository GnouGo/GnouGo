namespace GnOuGo.Agent.Shared;

public sealed record ChatMessageDto(
    string Role,
    string Content,
    string? MessageId = null,
    string? CorrelationId = null,
    string? TraceId = null);

public sealed record ChatStreamRequestDto(
    IReadOnlyList<ChatMessageDto> Messages,
    string? AgentName = null,
    IReadOnlyList<string>? FilesIds = null,
    string? ConversationId = null,
    string? Prompt = null);

public sealed record ChatCompletionResponseDto(string Text, string? ConversationId = null);

public sealed record ChatConversationSummaryDto(
    string ConversationId,
    string Title,
    long UpdatedAtUnixMs,
    int MessageCount);

public sealed record AppVersionDto(
    string Version,
    string ShortVersion);

// Browser-side persisted store (localStorage)
public sealed record ChatSessionDto(
    string Id,
    string Title,
    long UpdatedAtUnixMs,
    List<ChatMessageDto> Messages,
    string? AgentName = null,
    string? ConversationId = null);

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
