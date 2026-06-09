using System.Collections.Concurrent;
using System.Text.Json;

namespace GnOuGo.Agent.Mcp;

/// <summary>
/// In-memory chat history store.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public sealed class InMemoryChatHistoryStore
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _conversations = new();

    public ChatHistoryGetResult GetMessages(string conversationId, int topK)
    {
        var result = new ChatHistoryGetResult { ConversationId = conversationId };

        if (_conversations.TryGetValue(conversationId, out var messages))
        {
            lock (messages)
            {
                var count = Math.Min(topK, messages.Count);
                result.Messages = messages.Skip(messages.Count - count).Take(count)
                    .Select(m => new ChatMessage
                    {
                        Role = m.Role,
                        Content = m.Content,
                        CreatedAt = m.CreatedAt,
                        Meta = m.Meta?.Clone()
                    })
                    .ToList();
            }
        }

        return result;
    }

    public ChatHistoryAppendResult AppendMessages(string? conversationId, List<ChatMessage> messages)
    {
        conversationId ??= Guid.NewGuid().ToString("N")[..12];

        var conversation = _conversations.GetOrAdd(conversationId, _ => new List<ChatMessage>());

        lock (conversation)
        {
            conversation.AddRange(messages);
        }

        return new ChatHistoryAppendResult
        {
            ConversationId = conversationId,
            CountAppended = messages.Count
        };
    }

    public IReadOnlyList<ChatConversationSummary> ListConversations()
    {
        var summaries = new List<ChatConversationSummary>();

        foreach (var (conversationId, messages) in _conversations)
        {
            lock (messages)
            {
                if (messages.Count == 0)
                    continue;

                summaries.Add(new ChatConversationSummary
                {
                    ConversationId = conversationId,
                    Title = BuildTitle(messages),
                    UpdatedAt = messages.Max(static message => message.CreatedAt),
                    MessageCount = messages.Count
                });
            }
        }

        return summaries
            .OrderByDescending(static summary => summary.UpdatedAt)
            .ThenBy(static summary => summary.ConversationId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildTitle(IReadOnlyList<ChatMessage> messages)
    {
        var source = messages.FirstOrDefault(static message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(message.Content))
            ?? messages.FirstOrDefault(static message => !string.IsNullOrWhiteSpace(message.Content));

        if (source is null)
            return "Chat";

        var normalized = string.Join(" ", source.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
            return "Chat";

        return normalized.Length <= 48 ? normalized : normalized[..45].TrimEnd() + "...";
    }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public JsonElement? Meta { get; set; }
}

public sealed class ChatHistoryGetResult
{
    public string ConversationId { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
}

public sealed class ChatHistoryAppendResult
{
    public string ConversationId { get; set; } = "";
    public int CountAppended { get; set; }
}

public sealed class ChatConversationSummary
{
    public string ConversationId { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}
