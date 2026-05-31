using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp.Models;

namespace GnOuGo.Agent.Mcp;

public sealed record AgentToolResult(
    bool Success,
    AgentDto? Agent = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record AgentListToolResult(
    bool Success,
    IReadOnlyList<AgentDto>? Agents = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record AgentDeleteToolResult(
    bool Success,
    [property: JsonPropertyName("deleted_id")] string? DeletedId = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record AgentDto(
    string Id,
    string Name,
    string Workflow,
    [property: JsonPropertyName("original_prompt")] string? OriginalPrompt,
    [property: JsonPropertyName("schedule_description")] string? ScheduleDescription,
    IReadOnlyList<Schedule> Schedules,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

public sealed record ChatHistoryAppendToolResult(
    bool Success,
    [property: JsonPropertyName("conversation_id")] string? ConversationId = null,
    [property: JsonPropertyName("count_appended")] int? CountAppended = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record ChatHistoryGetToolResult(
    bool Success,
    [property: JsonPropertyName("conversation_id")] string? ConversationId = null,
    IReadOnlyList<ChatHistoryMessageDto>? Messages = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record ChatHistoryMessageInput(
    string? Role,
    string? Content,
    JsonElement? Meta = null);

public sealed record ChatHistoryMessageDto(
    string Role,
    string Content,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Meta = null);

public sealed record UserConfigToolResult(
    bool Success,
    UserConfigDto? Config = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null);

public sealed record UserConfigDto(
    [property: JsonPropertyName("default_llm_provider")] string? DefaultLlmProvider,
    [property: JsonPropertyName("default_llm_model")] string? DefaultLlmModel,
    [property: JsonPropertyName("default_embedding_config")] string? DefaultEmbeddingConfig,
    [property: JsonPropertyName("default_agent")] string? DefaultAgent,
    [property: JsonPropertyName("model_overrides")] IReadOnlyDictionary<string, LLMModelMetadata> ModelOverrides,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt);

public sealed record HealthResponse(string Status);

internal sealed class AgentSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Workflow { get; set; } = "";
    public string OriginalPrompt { get; set; } = "";
    public string ScheduleDescription { get; set; } = "";
    public ScheduleSnapshot[] Schedules { get; set; } = [];
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

internal sealed class ScheduleSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Cron { get; set; } = "";
}

