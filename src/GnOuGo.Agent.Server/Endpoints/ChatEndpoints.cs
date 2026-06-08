using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Agent.Shared;

namespace GnOuGo.Agent.Server.Endpoints;

public static class ChatEndpoints
{
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
                // SSE format: "event: <type>\ndata: <text>\n\n"
                await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {evt.Text ?? ""}\n\n", ct).ConfigureAwait(false);
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
}
