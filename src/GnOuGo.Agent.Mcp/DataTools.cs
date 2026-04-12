using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public JsonObject ChatHistoryAppend(
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
            return ErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "user_chat_history_append JSON parse error");
            return ErrorResult("INVALID_JSON", $"Failed to parse messagesJson: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_chat_history_append unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "user_chat_history_get"), Description(
        "Retrieve chat history messages for a conversation. " +
        "Returns { success, conversation_id, messages: [{ role, content, created_at, meta? }] } or { success: false, error_code, error_message }.")]
    public JsonObject ChatHistoryGet(
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
            return ErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_chat_history_get unexpected error for conversationId={ConversationId}", conversationId);
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "user_config_get"), Description(
        "Retrieve persisted user defaults for the local agent experience. " +
        "Returns { success, config: { default_llm_provider?, default_llm_model?, default_agent?, updated_at? } }.")]
    public async Task<JsonObject> UserConfigGet()
    {
        try
        {
            return SerializeUserConfig(await _userConfigs.GetAsync());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_config_get unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "user_config_set"), Description(
        "Persist user defaults in the Agent MCP database. All arguments are optional. " +
        "Provide default_llm_provider/default_llm_model and/or default_agent to update values. " +
        "Use clear_default_llm or clear_default_agent to remove persisted defaults. " +
        "Returns { success, config: { default_llm_provider?, default_llm_model?, default_agent?, updated_at? } }.")]
    public async Task<JsonObject> UserConfigSet(
        [Description("Default LLM provider name to persist.")]
        string? defaultLlmProvider = null,
        [Description("Default LLM model name to persist.")]
        string? defaultLlmModel = null,
        [Description("Default agent name to persist.")]
        string? defaultAgent = null,
        [Description("When true, clears both default_llm_provider and default_llm_model.")]
        bool clearDefaultLlm = false,
        [Description("When true, clears default_agent.")]
        bool clearDefaultAgent = false)
    {
        try
        {
            var snapshot = await _userConfigs.SetAsync(new UserConfigUpdate(
                DefaultLlmProvider: defaultLlmProvider,
                DefaultLlmModel: defaultLlmModel,
                DefaultAgent: defaultAgent,
                ClearDefaultLlm: clearDefaultLlm,
                ClearDefaultAgent: clearDefaultAgent));

            return SerializeUserConfig(snapshot);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "user_config_set validation error");
            return ErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "user_config_set unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Core logic (separated for testability) ──────────────────────────

    internal JsonObject ChatHistoryAppendCore(string messagesJson, string? conversationId)
    {
        var messagesNode = JsonNode.Parse(messagesJson);
        if (messagesNode is not JsonArray messagesArr || messagesArr.Count == 0)
            throw new ArgumentException("'messagesJson' must be a non-empty JSON array of messages.");

        var messages = new List<ChatMessage>();
        for (int i = 0; i < messagesArr.Count; i++)
        {
            if (messagesArr[i] is not JsonObject msgObj)
                throw new ArgumentException($"messages[{i}] must be a JSON object.");

            var role = msgObj["role"]?.GetValue<string>();
            var content = msgObj["content"]?.GetValue<string>();

            if (string.IsNullOrEmpty(role))
                throw new ArgumentException($"messages[{i}].role is required.");
            if (content == null)
                throw new ArgumentException($"messages[{i}].content is required.");

            var msg = new ChatMessage
            {
                Role = role,
                Content = content,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            if (msgObj.TryGetPropertyValue("meta", out var metaNode) && metaNode != null)
                msg.Meta = metaNode.DeepClone();

            messages.Add(msg);
        }

        var result = _store.AppendMessages(conversationId, messages);

        return new JsonObject
        {
            ["success"] = true,
            ["conversation_id"] = result.ConversationId,
            ["count_appended"] = result.CountAppended
        };
    }

    internal JsonObject ChatHistoryGetCore(string conversationId, int topK)
    {
        if (string.IsNullOrEmpty(conversationId))
            throw new ArgumentException("'conversationId' is required.");
        if (topK <= 0)
            throw new ArgumentException("'topK' must be > 0.");

        var result = _store.GetMessages(conversationId, topK);

        var messagesArray = new JsonArray();
        foreach (var msg in result.Messages)
        {
            var msgObj = new JsonObject
            {
                ["role"] = msg.Role,
                ["content"] = msg.Content,
                ["created_at"] = msg.CreatedAt.ToString("o")
            };
            if (msg.Meta != null)
                msgObj["meta"] = msg.Meta.DeepClone();
            messagesArray.Add((JsonNode)msgObj);
        }

        return new JsonObject
        {
            ["success"] = true,
            ["conversation_id"] = result.ConversationId,
            ["messages"] = messagesArray
        };
    }

    internal static JsonObject SerializeUserConfig(UserConfigSnapshot snapshot)
        => new()
        {
            ["success"] = true,
            ["config"] = new JsonObject
            {
                ["default_llm_provider"] = snapshot.DefaultLlmProvider,
                ["default_llm_model"] = snapshot.DefaultLlmModel,
                ["default_agent"] = snapshot.DefaultAgent,
                ["updated_at"] = snapshot.UpdatedAt?.ToString("o")
            }
        };

    private static JsonObject ErrorResult(string errorCode, string errorMessage)
        => new()
        {
            ["success"] = false,
            ["error_code"] = errorCode,
            ["error_message"] = errorMessage
        };
}

