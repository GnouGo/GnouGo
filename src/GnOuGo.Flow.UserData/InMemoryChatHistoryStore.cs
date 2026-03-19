using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.UserData;

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
                        Meta = m.Meta?.DeepClone()
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
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public JsonNode? Meta { get; set; }
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

