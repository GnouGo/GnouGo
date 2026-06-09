using Xunit;

namespace GnOuGo.Agent.Mcp.Tests;

public class InMemoryChatHistoryStoreTests
{
    private readonly InMemoryChatHistoryStore _store = new();

    // ── AppendMessages ───────────────────────────────────────────────

    [Fact]
    public void AppendMessages_GeneratesConversationId_WhenNull()
    {
        var result = _store.AppendMessages(null, [new ChatMessage { Role = "user", Content = "hi" }]);

        Assert.NotNull(result.ConversationId);
        Assert.NotEmpty(result.ConversationId);
        Assert.Equal(1, result.CountAppended);
    }

    [Fact]
    public void AppendMessages_UsesProvidedConversationId()
    {
        var result = _store.AppendMessages("conv-42", [new ChatMessage { Role = "user", Content = "hello" }]);

        Assert.Equal("conv-42", result.ConversationId);
        Assert.Equal(1, result.CountAppended);
    }

    [Fact]
    public void AppendMessages_AppendsMultipleMessages()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = "one" },
            new() { Role = "assistant", Content = "two" },
            new() { Role = "user", Content = "three" }
        };

        var result = _store.AppendMessages("multi", messages);

        Assert.Equal(3, result.CountAppended);
    }

    [Fact]
    public void AppendMessages_AccumulatesAcrossCalls()
    {
        _store.AppendMessages("acc", [new ChatMessage { Role = "user", Content = "first" }]);
        _store.AppendMessages("acc", [new ChatMessage { Role = "assistant", Content = "second" }]);

        var get = _store.GetMessages("acc", 100);

        Assert.Equal(2, get.Messages.Count);
        Assert.Equal("first", get.Messages[0].Content);
        Assert.Equal("second", get.Messages[1].Content);
    }

    // ── GetMessages ──────────────────────────────────────────────────

    [Fact]
    public void GetMessages_ReturnsEmpty_WhenConversationDoesNotExist()
    {
        var result = _store.GetMessages("nonexistent", 10);

        Assert.Equal("nonexistent", result.ConversationId);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void GetMessages_ReturnsMostRecent_WhenTopKLessThanTotal()
    {
        _store.AppendMessages("topk",
        [
            new ChatMessage { Role = "user", Content = "A" },
            new ChatMessage { Role = "assistant", Content = "B" },
            new ChatMessage { Role = "user", Content = "C" }
        ]);

        var result = _store.GetMessages("topk", 2);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("B", result.Messages[0].Content);
        Assert.Equal("C", result.Messages[1].Content);
    }

    [Fact]
    public void GetMessages_ReturnsAll_WhenTopKGreaterThanTotal()
    {
        _store.AppendMessages("all", [new ChatMessage { Role = "user", Content = "only" }]);

        var result = _store.GetMessages("all", 999);

        Assert.Single(result.Messages);
        Assert.Equal("only", result.Messages[0].Content);
    }

    [Fact]
    public void GetMessages_PreservesMeta()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"tag":"original"}""");
        var meta = document.RootElement.Clone();
        _store.AppendMessages("meta-test", [new ChatMessage { Role = "user", Content = "x", Meta = meta }]);

        var result = _store.GetMessages("meta-test", 10);

        Assert.NotNull(result.Messages[0].Meta);
        Assert.Equal("original", result.Messages[0].Meta!.Value.GetProperty("tag").GetString());
    }

    [Fact]
    public void ListConversations_ReturnsSummariesOrderedByLatestMessage()
    {
        var older = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var newer = DateTimeOffset.Parse("2026-01-02T10:00:00Z");

        _store.AppendMessages("older",
        [
            new ChatMessage { Role = "user", Content = "Older question", CreatedAt = older },
            new ChatMessage { Role = "assistant", Content = "Older answer", CreatedAt = older.AddMinutes(1) }
        ]);
        _store.AppendMessages("newer",
        [
            new ChatMessage { Role = "system", Content = "System prompt", CreatedAt = newer },
            new ChatMessage { Role = "user", Content = "Newer question with a useful title", CreatedAt = newer.AddMinutes(1) }
        ]);

        var summaries = _store.ListConversations();

        Assert.Equal(["newer", "older"], summaries.Select(static summary => summary.ConversationId).ToArray());
        Assert.Equal("Newer question with a useful title", summaries[0].Title);
        Assert.Equal(2, summaries[0].MessageCount);
        Assert.Equal(newer.AddMinutes(1), summaries[0].UpdatedAt);
    }

    // ── Thread safety (basic smoke test) ─────────────────────────────

    [Fact]
    public async Task AppendMessages_IsThreadSafe_ConcurrentAppends()
    {
        const int iterations = 200;
        var tasks = new List<Task>();

        for (int i = 0; i < iterations; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
                _store.AppendMessages("concurrent", [new ChatMessage { Role = "user", Content = $"msg-{idx}" }])));
        }

        await Task.WhenAll(tasks);

        var result = _store.GetMessages("concurrent", 1000);
        Assert.Equal(iterations, result.Messages.Count);
    }
}

