using System.ComponentModel;
using System.Text.Json;
using GnOuGo.AI.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using GnOuGo.Agent.Mcp.Services;

namespace GnOuGo.Agent.Mcp;

[McpServerToolType]
public sealed class DataTools
{
    private readonly InMemoryChatHistoryStore _store;
    private readonly IUserConfigRepository _userConfigs;
    private readonly ILogger<DataTools> _logger;

    public DataTools(InMemoryChatHistoryStore store, IUserConfigRepository userConfigs, ILogger<DataTools> logger)
    {
        _store = store;
        _userConfigs = userConfigs;
        _logger = logger;
    }

    [McpServerTool(Name = "user_chat_history_append"), Description(
        "Append messages to a chat conversation. If conversation_id is null or omitted, a new conversation is created. " +
        "Returns { success, conversation_id, count_appended } or { success: false, error_code, error_message }.")]
    public ChatHistoryAppendToolResult ChatHistoryAppend(
        [Description("JSON array of messages, each with 'role' (string) and 'content' (string), and optional 'meta' (object). Example: [{\"role\":\"user\",\"content\":\"Hello\"}]")]
        string messagesJson,
        [Description("Existing conversation id. Omit or pass null to create a new conversation.")]
        string? conversationId = null)
    {
        try
        {
            return ChatHistoryAppendCore(messagesJson, conversationId);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "user_chat_history_append validation error");
            return ChatHistoryAppendError("INVALID_INPUT", ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "user_chat_history_append JSON parse error");
            return ChatHistoryAppendError("INVALID_JSON", $"Failed to parse messagesJson: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_chat_history_append unexpected error");
            return ChatHistoryAppendError("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "user_chat_history_get"), Description(
        "Retrieve chat history messages for a conversation. " +
        "Returns { success, conversation_id, messages: [{ role, content, created_at, meta? }] } or { success: false, error_code, error_message }.")]
    public ChatHistoryGetToolResult ChatHistoryGet(
        [Description("The conversation identifier (required).")]
        string conversationId,
        [Description("Maximum number of messages to return (most recent). Default: 50.")]
        int topK = 50)
    {
        try
        {
            return ChatHistoryGetCore(conversationId, topK);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "user_chat_history_get validation error for conversationId={ConversationId}", conversationId);
            return ChatHistoryGetError("INVALID_INPUT", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_chat_history_get unexpected error for conversationId={ConversationId}", conversationId);
            return ChatHistoryGetError("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "user_config_get"), Description(
        "Retrieve persisted user defaults for the local agent experience. " +
        "Returns { success, config: { default_llm_provider?, default_llm_model?, default_embedding_config?, default_agent?, updated_at? } }.")]
    public async Task<UserConfigToolResult> UserConfigGet()
    {
        try
        {
            return SerializeUserConfig(await _userConfigs.GetAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_config_get unexpected error");
            return UserConfigError("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "user_config_set"), Description(
        "Persist user defaults in the Agent MCP database. All arguments are optional. " +
        "Provide default_llm_provider/default_llm_model, default_embedding_config and/or default_agent to update values. " +
        "Provide model_overrides_json as a JSON object keyed by model id to persist custom model metadata. " +
        "Use clear_default_llm, clear_default_embedding or clear_default_agent to remove persisted defaults. " +
        "Returns { success, config: { default_llm_provider?, default_llm_model?, default_embedding_config?, default_agent?, model_overrides?, updated_at? } }.")]
    public async Task<UserConfigToolResult> UserConfigSet(
        [Description("Default LLM provider name to persist.")]
        string? defaultLlmProvider = null,
        [Description("Default LLM model name to persist.")]
        string? defaultLlmModel = null,
        [Description("Default embedding configuration name to persist.")]
        string? defaultEmbeddingConfig = null,
        [Description("Default agent name to persist.")]
        string? defaultAgent = null,
        [Description("When true, clears both default_llm_provider and default_llm_model.")]
        bool clearDefaultLlm = false,
        [Description("When true, clears default_embedding_config.")]
        bool clearDefaultEmbedding = false,
        [Description("When true, clears default_agent.")]
        bool clearDefaultAgent = false,
        [Description("JSON object of LLM model metadata overrides, keyed by model id. Pass {} to clear all overrides.")]
        string? modelOverridesJson = null)
    {
        try
        {
            var modelOverrides = ParseModelOverrides(modelOverridesJson);
            var snapshot = await _userConfigs.SetAsync(new UserConfigUpdate(
                DefaultLlmProvider: defaultLlmProvider,
                DefaultLlmModel: defaultLlmModel,
                DefaultEmbeddingConfig: defaultEmbeddingConfig,
                DefaultAgent: defaultAgent,
                ClearDefaultLlm: clearDefaultLlm,
                ClearDefaultEmbedding: clearDefaultEmbedding,
                ClearDefaultAgent: clearDefaultAgent,
                ModelOverrides: modelOverrides));

            return SerializeUserConfig(snapshot);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "user_config_set validation error");
            return UserConfigError("INVALID_INPUT", ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "user_config_set model overrides JSON parse error");
            return UserConfigError("INVALID_JSON", $"Failed to parse modelOverridesJson: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_config_set unexpected error");
            return UserConfigError("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal ChatHistoryAppendToolResult ChatHistoryAppendCore(string messagesJson, string? conversationId)
    {
        var inputs = JsonSerializer.Deserialize(messagesJson, AgentMcpJsonContext.Default.ListChatHistoryMessageInput);
        if (inputs is null || inputs.Count == 0)
            throw new ArgumentException("'messagesJson' must be a non-empty JSON array of messages.");

        var messages = new List<ChatMessage>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (string.IsNullOrEmpty(input.Role))
                throw new ArgumentException($"messages[{i}].role is required.");
            if (input.Content is null)
                throw new ArgumentException($"messages[{i}].content is required.");

            var msg = new ChatMessage
            {
                Role = input.Role,
                Content = input.Content,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            if (input.Meta is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } meta)
                msg.Meta = meta.Clone();

            messages.Add(msg);
        }

        var result = _store.AppendMessages(conversationId, messages);
        return new ChatHistoryAppendToolResult(true, result.ConversationId, result.CountAppended);
    }

    internal ChatHistoryGetToolResult ChatHistoryGetCore(string conversationId, int topK)
    {
        if (string.IsNullOrEmpty(conversationId))
            throw new ArgumentException("'conversationId' is required.");
        if (topK <= 0)
            throw new ArgumentException("'topK' must be > 0.");

        var result = _store.GetMessages(conversationId, topK);
        var messages = result.Messages
            .Select(static msg => new ChatHistoryMessageDto(
                msg.Role,
                msg.Content,
                msg.CreatedAt.ToString("o"),
                msg.Meta?.Clone()))
            .ToArray();

        return new ChatHistoryGetToolResult(true, result.ConversationId, messages);
    }

    internal static UserConfigToolResult SerializeUserConfig(UserConfigSnapshot snapshot)
        => new(true, new UserConfigDto(
            snapshot.DefaultLlmProvider,
            snapshot.DefaultLlmModel,
            snapshot.DefaultEmbeddingConfig,
            snapshot.DefaultAgent,
            snapshot.ModelOverrides ?? new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase),
            snapshot.UpdatedAt?.ToString("o")));

    private static IReadOnlyDictionary<string, LLMModelMetadata>? ParseModelOverrides(string? modelOverridesJson)
    {
        if (modelOverridesJson is null)
            return null;

        return JsonSerializer.Deserialize(modelOverridesJson, AgentMcpJsonContext.Default.DictionaryStringLLMModelMetadata)
               ?? new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase);
    }

    private static ChatHistoryAppendToolResult ChatHistoryAppendError(string errorCode, string errorMessage)
        => new(false, ErrorCode: errorCode, ErrorMessage: errorMessage);

    private static ChatHistoryGetToolResult ChatHistoryGetError(string errorCode, string errorMessage)
        => new(false, ErrorCode: errorCode, ErrorMessage: errorMessage);

    private static UserConfigToolResult UserConfigError(string errorCode, string errorMessage)
        => new(false, ErrorCode: errorCode, ErrorMessage: errorMessage);
}

