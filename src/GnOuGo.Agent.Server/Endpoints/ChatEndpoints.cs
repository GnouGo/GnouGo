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
        if (request.Messages is null || request.Messages.Count == 0)
            return Results.BadRequest("messages is required.");

        try
        {
            var lastUserMsg = "";
            for (var i = request.Messages.Count - 1; i >= 0; i--)
            {
                if (string.Equals(request.Messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    lastUserMsg = request.Messages[i].Content;
                    break;
                }
            }

            var text = await smartFlow.CompleteAsync(lastUserMsg, request.AgentName, request.FilesIds, ct).ConfigureAwait(false);
            return Results.Ok(new ChatCompletionResponseDto(text));
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

        if (request.Messages is null || request.Messages.Count == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("messages is required.", ct);
            return;
        }

        try
        {
            var lastUserMsg = "";
            for (var i = request.Messages.Count - 1; i >= 0; i--)
            {
                if (string.Equals(request.Messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    lastUserMsg = request.Messages[i].Content;
                    break;
                }
            }

            await foreach (var evt in smartFlow.ExecuteAsync(lastUserMsg, correlationId: null, agentName: request.AgentName, filesIds: request.FilesIds, ct).ConfigureAwait(false))
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
}
