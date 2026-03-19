using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using GnOuGo.Agent.Server.OpenAI;
using GnOuGo.Agent.Shared;

namespace GnOuGo.Agent.Server.Endpoints;

public static class ChatEndpoints
{
    public static async Task<IResult> CompleteAsync(
        ChatStreamRequestDto request,
        OpenAIResponsesClient openAi,
        CancellationToken ct)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return Results.BadRequest("messages is required.");

        try
        {
            var text = await openAi.CompleteAsync(request.Messages, ct).ConfigureAwait(false);
            return Results.Ok(new { text });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    public static async Task StreamAsync(
        HttpContext ctx,
        ChatStreamRequestDto request,
        OpenAIResponsesClient openAi,
        CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.Pragma = "no-cache";
        ctx.Response.Headers.Append("X-Accel-Buffering", "no");
        ctx.Response.ContentType = "text/plain; charset=utf-8";

        if (request.Messages is null || request.Messages.Count == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("messages is required.", ct);
            return;
        }

        try
        {
            await foreach (var chunk in openAi.StreamChatWordsAsync(request.Messages, ct).ConfigureAwait(false))
            {
                await ctx.Response.WriteAsync(chunk, ct).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected / cancelled: nothing to do
        }
        catch (Exception ex)
        {
            // Best-effort error write (might be too late if headers are sent)
            try
            {
                await ctx.Response.WriteAsync($"\n\n[error] {ex.Message}", ct).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
            catch { }
        }
    }
}
