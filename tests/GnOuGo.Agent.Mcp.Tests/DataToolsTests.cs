using Microsoft.Extensions.Logging.Abstractions;
using GnOuGo.Agent.Mcp.Services;
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
    [Fact]
    public void ChatHistoryAppend_CreatesNewConversation_WhenIdIsNull()
    {
        var result = _tools.ChatHistoryAppend("""[{"role":"user","content":"hello"}]""");
        Assert.True(result.Success);
        Assert.NotNull(result.ConversationId);
        Assert.Equal(1, result.CountAppended);
    }
    [Fact]
    public void ChatHistoryAppend_UsesExistingConversationId()
    {
        var result = _tools.ChatHistoryAppend("""[{"role":"user","content":"hi"}]""", "existing-conv");
        Assert.True(result.Success);
        Assert.Equal("existing-conv", result.ConversationId);
    }
    [Fact]
    public void ChatHistoryAppend_ParsesMultipleMessages()
    {
        var result = _tools.ChatHistoryAppend("""[{"role":"user","content":"A"},{"role":"assistant","content":"B"}]""");
        Assert.True(result.Success);
        Assert.Equal(2, result.CountAppended);
    }
    [Fact]
    public void ChatHistoryAppend_PreservesMetaProperty()
    {
        var appendResult = _tools.ChatHistoryAppend("""[{"role":"user","content":"test","meta":{"source":"unit-test","score":42}}]""", "meta-conv");
        Assert.True(appendResult.Success);
        var getResult = _tools.ChatHistoryGet(appendResult.ConversationId!);
        Assert.True(getResult.Success);
        Assert.NotNull(getResult.Messages);
        var message = Assert.Single(getResult.Messages);
        Assert.NotNull(message.Meta);
        Assert.Equal("unit-test", message.Meta.Value.GetProperty("source").GetString());
        Assert.Equal(42, message.Meta.Value.GetProperty("score").GetInt32());
    }
    [Theory]
    [InlineData("""{"role":"user"}""", "INVALID_JSON", null)]
    [InlineData("[]", "INVALID_INPUT", null)]
    [InlineData("""[{"content":"hello"}]""", "INVALID_INPUT", "role is required")]
    [InlineData("""[{"role":"user"}]""", "INVALID_INPUT", "content is required")]
    [InlineData("""["not an object"]""", "INVALID_JSON", null)]
    public void ChatHistoryAppend_ReturnsValidationErrors(string json, string expectedCode, string? expectedMessagePart)
    {
        var result = _tools.ChatHistoryAppend(json);
        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.ErrorCode);
        if (expectedMessagePart is not null)
            Assert.Contains(expectedMessagePart, result.ErrorMessage);
    }
    [Fact]
    public void ChatHistoryAppend_ReturnsError_WhenJsonIsMalformed()
    {
        var result = _tools.ChatHistoryAppend("not json at all {{{");
        Assert.False(result.Success);
        Assert.Equal("INVALID_JSON", result.ErrorCode);
    }
    [Fact]
    public void ChatHistoryGet_ReturnsEmptyMessages_WhenConversationDoesNotExist()
    {
        var result = _tools.ChatHistoryGet("unknown");
        Assert.True(result.Success);
        Assert.Equal("unknown", result.ConversationId);
        Assert.NotNull(result.Messages);
        Assert.Empty(result.Messages);
    }
    [Fact]
    public void ChatHistoryGet_ReturnsMessages_WithCorrectFields()
    {
        _tools.ChatHistoryAppend("""[{"role":"user","content":"question"},{"role":"assistant","content":"answer"}]""", "fields-test");
        var result = _tools.ChatHistoryGet("fields-test");
        Assert.True(result.Success);
        Assert.NotNull(result.Messages);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("question", result.Messages[0].Content);
        Assert.NotNull(result.Messages[0].CreatedAt);
    }
    [Fact]
    public void ChatHistoryGet_RespectsTopK()
    {
        _tools.ChatHistoryAppend("""[{"role":"user","content":"1"},{"role":"user","content":"2"},{"role":"user","content":"3"}]""", "topk-test");
        var result = _tools.ChatHistoryGet("topk-test", topK: 2);
        Assert.True(result.Success);
        Assert.NotNull(result.Messages);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("2", result.Messages[0].Content);
        Assert.Equal("3", result.Messages[1].Content);
    }
    [Theory]
    [InlineData("", 50, "conversationId")]
    [InlineData("any", 0, "topK")]
    public void ChatHistoryGet_ReturnsValidationErrors(string conversationId, int topK, string expectedMessagePart)
    {
        var result = _tools.ChatHistoryGet(conversationId, topK);
        Assert.False(result.Success);
        Assert.Equal("INVALID_INPUT", result.ErrorCode);
        Assert.Contains(expectedMessagePart, result.ErrorMessage);
    }
    [Fact]
    public void RoundTrip_AppendThenGet_ReturnsConsistentData()
    {
        var appendResult = _tools.ChatHistoryAppend("""[{"role":"system","content":"You are helpful."},{"role":"user","content":"What is 2+2?"}]""");
        Assert.True(appendResult.Success);
        var convId = appendResult.ConversationId!;
        _tools.ChatHistoryAppend("""[{"role":"assistant","content":"4"}]""", convId);
        var getResult = _tools.ChatHistoryGet(convId, topK: 100);
        Assert.True(getResult.Success);
        Assert.NotNull(getResult.Messages);
        Assert.Equal(3, getResult.Messages.Count);
        Assert.Equal("system", getResult.Messages[0].Role);
        Assert.Equal("user", getResult.Messages[1].Role);
        Assert.Equal("assistant", getResult.Messages[2].Role);
        Assert.Equal("4", getResult.Messages[2].Content);
    }
    [Fact]
    public async Task UserConfigGet_ReturnsEmptyConfig_WhenNothingWasSaved()
    {
        var result = await _tools.UserConfigGet();
        Assert.True(result.Success);
        Assert.NotNull(result.Config);
        Assert.Null(result.Config.DefaultLlmProvider);
        Assert.Null(result.Config.DefaultLlmModel);
        Assert.Null(result.Config.DefaultAgent);
    }
    [Fact]
    public async Task UserConfigSet_SavesAndReturnsDefaultValues()
    {
        var result = await _tools.UserConfigSet(
            defaultLlmProvider: "ollama",
            defaultLlmModel: "llama3:8b",
            defaultAgent: "slimfaas");
        Assert.True(result.Success);
        Assert.NotNull(result.Config);
        Assert.Equal("ollama", result.Config.DefaultLlmProvider);
        Assert.Equal("llama3:8b", result.Config.DefaultLlmModel);
        Assert.Equal("slimfaas", result.Config.DefaultAgent);
        Assert.NotNull(result.Config.UpdatedAt);
    }
    [Fact]
    public async Task UserConfigSet_ClearsSelectedValues_WhenRequested()
    {
        await _tools.UserConfigSet(defaultLlmProvider: "openai", defaultLlmModel: "gpt-4o-mini", defaultAgent: "slimfaas");
        var result = await _tools.UserConfigSet(clearDefaultLlm: true, clearDefaultAgent: true);
        Assert.NotNull(result.Config);
        Assert.Null(result.Config.DefaultLlmProvider);
        Assert.Null(result.Config.DefaultLlmModel);
        Assert.Null(result.Config.DefaultAgent);
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
        Assert.True(result.Success);
        Assert.NotNull(result.Config);
        var custom = Assert.Single(result.Config.ModelOverrides).Value;
        Assert.Equal(32768, custom.ContextWindowTokens);
        Assert.True(custom.Capabilities.SupportsTools);
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
            var embedding = update.ClearDefaultEmbedding
                ? null
                : string.IsNullOrWhiteSpace(update.DefaultEmbeddingConfig) ? _snapshot.DefaultEmbeddingConfig : update.DefaultEmbeddingConfig.Trim();
            var agent = update.ClearDefaultAgent
                ? null
                : string.IsNullOrWhiteSpace(update.DefaultAgent) ? _snapshot.DefaultAgent : update.DefaultAgent.Trim();
            var overrides = update.ModelOverrides ?? _snapshot.ModelOverrides;
            _snapshot = new UserConfigSnapshot(provider, model, agent, DateTimeOffset.UtcNow, embedding, overrides);
            return Task.FromResult(_snapshot);
        }
    }
}




