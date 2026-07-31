using System.Text.Json;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Shared;
using GnOuGo.Agent.Mcp;

namespace GnOuGo.Agent.Server.Endpoints;

public static class ChatEndpoints
{
    public static IResult ListConversations(InMemoryChatHistoryStore historyStore)
    {
        var conversations = historyStore.ListConversations()
            .Select(static conversation => new ChatConversationSummaryDto(
                conversation.ConversationId,
                conversation.Title,
                conversation.UpdatedAt.ToUnixTimeMilliseconds(),
                conversation.MessageCount))
            .ToList();

        return Results.Ok(conversations);
    }

    public static IResult GetConversation(string conversationId, InMemoryChatHistoryStore historyStore)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return Results.BadRequest("conversationId is required.");

        var result = historyStore.GetMessages(conversationId, topK: int.MaxValue);
        if (result.Messages.Count == 0)
            return Results.NotFound();

        var messages = result.Messages
            .Select(static message => new ChatMessageDto(
                message.Role,
                message.Content,
                MessageId: Guid.NewGuid().ToString("N"),
                CorrelationId: TryGetMetaString(message.Meta, "correlation_id")))
            .ToList();

        var updatedAt = result.Messages.Max(static message => message.CreatedAt);
        var title = historyStore.ListConversations()
            .FirstOrDefault(summary => string.Equals(summary.ConversationId, conversationId, StringComparison.Ordinal))
            ?.Title ?? "Chat";

        return Results.Ok(new ChatSessionDto(
            Id: conversationId,
            Title: title,
            UpdatedAtUnixMs: updatedAt.ToUnixTimeMilliseconds(),
            Messages: messages,
            ConversationId: conversationId));
    }

    public static async Task<IResult> CompleteAsync(
        ChatStreamRequestDto request,
        SmartFlowService smartFlow,
        CancellationToken ct)
    {
        if ((request.Messages is null || request.Messages.Count == 0) && string.IsNullOrWhiteSpace(request.Prompt))
            return Results.BadRequest("messages is required.");

        try
        {
            var lastUserMsg = ResolvePrompt(request);

            var conversationId = request.ConversationId;
            var text = "";
            await foreach (var evt in smartFlow.ExecuteAsync(lastUserMsg, correlationId: null, request.AgentName, request.FilesIds, workflowInputs: null, conversationId, ct).ConfigureAwait(false))
            {
                if (evt.Type is "conversation" && !string.IsNullOrWhiteSpace(evt.ConversationId))
                    conversationId = evt.ConversationId;
                else if (evt.Type is "answer")
                    text = evt.Text ?? "";
                else if (evt.Type is "error")
                    throw new InvalidOperationException(evt.Text);
            }

            return Results.Ok(new ChatCompletionResponseDto(text, conversationId));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    public static async Task StreamAsync(
        HttpContext ctx,
        ChatStreamRequestDto request,
        SmartFlowService smartFlow,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(ChatEndpoints).FullName ?? nameof(ChatEndpoints));
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
        ctx.Response.Headers.Append("X-Accel-Buffering", "no");
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";

        if ((request.Messages is null || request.Messages.Count == 0) && string.IsNullOrWhiteSpace(request.Prompt))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("messages is required.", ct);
            return;
        }

        try
        {
            var lastUserMsg = ResolvePrompt(request);

            await foreach (var evt in smartFlow.ExecuteAsync(lastUserMsg, correlationId: null, request.AgentName, request.FilesIds, workflowInputs: null, request.ConversationId, ct).ConfigureAwait(false))
            {
                var data = evt.Animation is null
                    ? evt.Text ?? ""
                    : JsonSerializer.Serialize(
                        evt.Animation,
                        AgentAnimationJsonContext.Default.AnimationStreamPayload);
                await WriteSseEventAsync(ctx.Response, evt.Type, data, ct).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(ex, "Chat stream was cancelled, likely because the client disconnected.");
            // client disconnected
        }
        catch (Exception ex)
        {
            try
            {
                await ctx.Response.WriteAsync($"event: error\ndata: {ex.Message}\n\n", ct).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception writeEx)
            {
                logger.LogDebug(writeEx, "Failed to write chat stream error event to the response.");
            }
        }
    }

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventType,
        string data,
        CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventType}\n", ct).ConfigureAwait(false);
        using var reader = new StringReader(data);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            await response.WriteAsync($"data: {line}\n", ct).ConfigureAwait(false);
        if (data.Length == 0)
            await response.WriteAsync("data: \n", ct).ConfigureAwait(false);
        await response.WriteAsync("\n", ct).ConfigureAwait(false);
    }

    private static string ResolvePrompt(ChatStreamRequestDto request)
    {
        if (!string.IsNullOrWhiteSpace(request.Prompt))
            return request.Prompt;

        if (request.Messages is null)
            return "";

        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(request.Messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                return request.Messages[i].Content;
        }

        return "";
    }

    private static string? TryGetMetaString(System.Text.Json.JsonElement? meta, string propertyName)
    {
        if (meta is not { ValueKind: System.Text.Json.JsonValueKind.Object } element)
            return null;

        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == System.Text.Json.JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
