using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Mcp.Services;
using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public class DataToolsTests
{
    private readonly InMemoryChatHistoryStore _store = new();
    private readonly InMemoryUserConfigRepository _userConfigs = new();
    private readonly DataTools _tools;

    public DataToolsTests()
    {
        _tools = new DataTools(_store, _userConfigs, NullLogger<DataTools>.Instance);
    }

    // ── ChatHistoryAppend ────────────────────────────────────────────

    [Fact]
    public void ChatHistoryAppend_CreatesNewConversation_WhenIdIsNull()
    {
        var json = """[{"role":"user","content":"hello"}]""";

        var result = _tools.ChatHistoryAppend(json);

        Assert.True(result["success"]!.GetValue<bool>());
        Assert.NotNull(result["conversation_id"]?.GetValue<string>());
        Assert.Equal(1, result["count_appended"]!.GetValue<int>());
    }

    [Fact]
    public void ChatHistoryAppend_UsesExistingConversationId()
    {
        var json = """[{"role":"user","content":"hi"}]""";

        var result = _tools.ChatHistoryAppend(json, "existing-conv");

        Assert.True(result["success"]!.GetValue<bool>());
        Assert.Equal("existing-conv", result["conversation_id"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryAppend_ParsesMultipleMessages()
    {
        var json = """[{"role":"user","content":"A"},{"role":"assistant","content":"B"}]""";

        var result = _tools.ChatHistoryAppend(json);

        Assert.True(result["success"]!.GetValue<bool>());
        Assert.Equal(2, result["count_appended"]!.GetValue<int>());
    }

    [Fact]
    public void ChatHistoryAppend_PreservesMetaProperty()
    {
        var json = """[{"role":"user","content":"test","meta":{"source":"unit-test","score":42}}]""";

        var appendResult = _tools.ChatHistoryAppend(json, "meta-conv");
        Assert.True(appendResult["success"]!.GetValue<bool>());
        var convId = appendResult["conversation_id"]!.GetValue<string>();

        var getResult = _tools.ChatHistoryGet(convId);
        Assert.True(getResult["success"]!.GetValue<bool>());
        var messages = getResult["messages"]!.AsArray();
        Assert.Single(messages);

        var meta = messages[0]!.AsObject()["meta"]!.AsObject();
        Assert.Equal("unit-test", meta["source"]!.GetValue<string>());
        Assert.Equal(42, meta["score"]!.GetValue<int>());
    }

    [Fact]
    public void ChatHistoryAppend_ReturnsError_WhenJsonIsNotArray()
    {
        var result = _tools.ChatHistoryAppend("""{"role":"user"}""");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
        Assert.Contains("non-empty JSON array", result["error_message"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryAppend_ReturnsError_WhenArrayIsEmpty()
    {
        var result = _tools.ChatHistoryAppend("[]");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryAppend_ReturnsError_WhenMessageHasNoRole()
    {
        var json = """[{"content":"hello"}]""";
        var result = _tools.ChatHistoryAppend(json);

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
        Assert.Contains("role is required", result["error_message"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryAppend_ReturnsError_WhenMessageHasNoContent()
    {
        var json = """[{"role":"user"}]""";
        var result = _tools.ChatHistoryAppend(json);

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
        Assert.Contains("content is required", result["error_message"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryAppend_ReturnsError_WhenElementIsNotObject()
    {
        var json = """["not an object"]""";
        var result = _tools.ChatHistoryAppend(json);

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
        Assert.Contains("must be a JSON object", result["error_message"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryAppend_ReturnsError_WhenJsonIsMalformed()
    {
        var result = _tools.ChatHistoryAppend("not json at all {{{");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_JSON", result["error_code"]!.GetValue<string>());
    }

    // ── ChatHistoryGet ───────────────────────────────────────────────

    [Fact]
    public void ChatHistoryGet_ReturnsEmptyMessages_WhenConversationDoesNotExist()
    {
        var result = _tools.ChatHistoryGet("unknown");

        Assert.True(result["success"]!.GetValue<bool>());
        Assert.Equal("unknown", result["conversation_id"]!.GetValue<string>());
        Assert.Empty(result["messages"]!.AsArray());
    }

    [Fact]
    public void ChatHistoryGet_ReturnsMessages_WithCorrectFields()
    {
        var json = """[{"role":"user","content":"question"},{"role":"assistant","content":"answer"}]""";
        _tools.ChatHistoryAppend(json, "fields-test");

        var result = _tools.ChatHistoryGet("fields-test");
        Assert.True(result["success"]!.GetValue<bool>());
        var messages = result["messages"]!.AsArray();

        Assert.Equal(2, messages.Count);

        var first = messages[0]!.AsObject();
        Assert.Equal("user", first["role"]!.GetValue<string>());
        Assert.Equal("question", first["content"]!.GetValue<string>());
        Assert.NotNull(first["created_at"]?.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryGet_RespectsTopK()
    {
        var json = """[{"role":"user","content":"1"},{"role":"user","content":"2"},{"role":"user","content":"3"}]""";
        _tools.ChatHistoryAppend(json, "topk-test");

        var result = _tools.ChatHistoryGet("topk-test", topK: 2);
        Assert.True(result["success"]!.GetValue<bool>());
        var messages = result["messages"]!.AsArray();

        Assert.Equal(2, messages.Count);
        Assert.Equal("2", messages[0]!.AsObject()["content"]!.GetValue<string>());
        Assert.Equal("3", messages[1]!.AsObject()["content"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryGet_ReturnsError_WhenConversationIdIsEmpty()
    {
        var result = _tools.ChatHistoryGet("");

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
        Assert.Contains("conversationId", result["error_message"]!.GetValue<string>());
    }

    [Fact]
    public void ChatHistoryGet_ReturnsError_WhenTopKIsZeroOrNegative()
    {
        var result = _tools.ChatHistoryGet("any", topK: 0);

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.Equal("INVALID_INPUT", result["error_code"]!.GetValue<string>());
        Assert.Contains("topK", result["error_message"]!.GetValue<string>());
    }

    // ── Round-trip ───────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_AppendThenGet_ReturnsConsistentData()
    {
        var json = """[{"role":"system","content":"You are helpful."},{"role":"user","content":"What is 2+2?"}]""";
        var appendResult = _tools.ChatHistoryAppend(json);
        Assert.True(appendResult["success"]!.GetValue<bool>());
        var convId = appendResult["conversation_id"]!.GetValue<string>();

        // Append assistant response
        var json2 = """[{"role":"assistant","content":"4"}]""";
        _tools.ChatHistoryAppend(json2, convId);

        var getResult = _tools.ChatHistoryGet(convId, topK: 100);
        Assert.True(getResult["success"]!.GetValue<bool>());
        var messages = getResult["messages"]!.AsArray();

        Assert.Equal(3, messages.Count);
        Assert.Equal("system", messages[0]!.AsObject()["role"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!.AsObject()["role"]!.GetValue<string>());
        Assert.Equal("assistant", messages[2]!.AsObject()["role"]!.GetValue<string>());
        Assert.Equal("4", messages[2]!.AsObject()["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task UserConfigGet_ReturnsEmptyConfig_WhenNothingWasSaved()
    {
        var result = await _tools.UserConfigGet();

        Assert.True(result["success"]!.GetValue<bool>());
        var config = result["config"]!.AsObject();
        Assert.Null(config["default_llm_provider"]);
        Assert.Null(config["default_llm_model"]);
        Assert.Null(config["default_agent"]);
    }

    [Fact]
    public async Task UserConfigSet_SavesAndReturnsDefaultValues()
    {
        var result = await _tools.UserConfigSet(
            defaultLlmProvider: "ollama",
            defaultLlmModel: "llama3:8b",
            defaultAgent: "slimfaas");

        Assert.True(result["success"]!.GetValue<bool>());
        var config = result["config"]!.AsObject();
        Assert.Equal("ollama", config["default_llm_provider"]!.GetValue<string>());
        Assert.Equal("llama3:8b", config["default_llm_model"]!.GetValue<string>());
        Assert.Equal("slimfaas", config["default_agent"]!.GetValue<string>());
        Assert.NotNull(config["updated_at"]?.GetValue<string>());
    }

    [Fact]
    public async Task UserConfigSet_ClearsSelectedValues_WhenRequested()
    {
        await _tools.UserConfigSet(defaultLlmProvider: "openai", defaultLlmModel: "gpt-4o-mini", defaultAgent: "slimfaas");

        var result = await _tools.UserConfigSet(clearDefaultLlm: true, clearDefaultAgent: true);

        var config = result["config"]!.AsObject();
        Assert.Null(config["default_llm_provider"]);
        Assert.Null(config["default_llm_model"]);
        Assert.Null(config["default_agent"]);
    }

    [Fact]
    public async Task UserConfigSet_SavesAndReturnsModelOverrides()
    {
        var result = await _tools.UserConfigSet(
            modelOverridesJson: """
            {
              "local/custom": {
                "id": "local/custom",
                "providerType": "ollama",
                "contextWindowTokens": 32768,
                "maxInputTokens": 32768,
                "maxOutputTokens": 4096,
                "pricing": { "inputPer1MTokens": 0, "outputPer1MTokens": 0 },
                "capabilities": {
                  "supportsTemperature": true,
                  "supportsReasoningEffort": false,
                  "supportsStructuredOutput": true,
                  "supportsTools": true,
                  "supportsJsonMode": true
                }
              }
            }
            """);

        Assert.True(result["success"]!.GetValue<bool>());
        var overrides = result["config"]!.AsObject()["model_overrides"]!.AsObject();
        var custom = overrides["local/custom"]!.AsObject();
        Assert.Equal(32768, custom["contextWindowTokens"]!.GetValue<int>());
        Assert.True(custom["capabilities"]!.AsObject()["supportsTools"]!.GetValue<bool>());
    }

    private sealed class InMemoryUserConfigRepository : IUserConfigRepository
    {
        private UserConfigSnapshot _snapshot = new(null, null, null, null);

        public Task<UserConfigSnapshot> GetAsync(Guid? tenantId = null, CancellationToken ct = default)
            => Task.FromResult(_snapshot);

        public Task<UserConfigSnapshot> SetAsync(UserConfigUpdate update, Guid? tenantId = null, CancellationToken ct = default)
        {
            var provider = update.ClearDefaultLlm
                ? null
                : string.IsNullOrWhiteSpace(update.DefaultLlmProvider) ? _snapshot.DefaultLlmProvider : update.DefaultLlmProvider.Trim();
            var model = update.ClearDefaultLlm
                ? null
                : string.IsNullOrWhiteSpace(update.DefaultLlmModel) ? _snapshot.DefaultLlmModel : update.DefaultLlmModel.Trim();
            var agent = update.ClearDefaultAgent
                ? null
                : string.IsNullOrWhiteSpace(update.DefaultAgent) ? _snapshot.DefaultAgent : update.DefaultAgent.Trim();
            var overrides = update.ModelOverrides ?? _snapshot.ModelOverrides;

            _snapshot = new UserConfigSnapshot(provider, model, agent, DateTimeOffset.UtcNow, ModelOverrides: overrides);
            return Task.FromResult(_snapshot);
        }
    }
}

