using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Shared;
using GnOuGo.AI.Core;

namespace GnOuGo.Agent.Server.OpenAI;

/// <summary>
/// Minimal, AOT-friendly client for the OpenAI Responses API.
/// </summary>
public sealed class OpenAIResponsesClient
{
    private readonly HttpClient _http;
    private readonly OpenAIOptions _opt;
    private readonly GnOuGo.AI.Core.WordChunker _chunker;

    public OpenAIResponsesClient(HttpClient http, IOptions<OpenAIOptions> opt, GnOuGo.AI.Core.WordChunker chunker)
    {
        _http = http;
        _opt = opt.Value;
        _chunker = chunker;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessageDto> messages, CancellationToken ct)
    {
        var req = CreateRequest(messages, stream: false);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(req, OpenAIJsonContext.Default.OpenAIResponseRequest),
                Encoding.UTF8,
                "application/json")
        };

        ApplyAuth(httpReq);

        using var res = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var payload = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(ChatResponseParser.FormatHttpError(res.StatusCode, payload));

        using var doc = JsonDocument.Parse(payload);
        return ChatResponseParser.ExtractResponsesApiContent(doc.RootElement);
    }

    public async IAsyncEnumerable<string> StreamChatWordsAsync(
        IReadOnlyList<ChatMessageDto> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var req = CreateRequest(messages, stream: true);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "responses")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(req, OpenAIJsonContext.Default.OpenAIResponseRequest),
                Encoding.UTF8,
                "application/json")
        };

        ApplyAuth(httpReq);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var res = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!res.IsSuccessStatusCode)
        {
            var payload = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(ChatResponseParser.FormatHttpError(res.StatusCode, payload));
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var state = _chunker.Create();

        await foreach (var evt in SseParser.ReadEventsAsync(stream, ct).ConfigureAwait(false))
        {
            using (evt)
            {
                if (!evt.RootElement.TryGetProperty("type", out var typeProp))
                    continue;

                var type = typeProp.GetString();
                if (type is "response.output_text.delta")
                {
                    var delta = evt.RootElement.GetProperty("delta").GetString();
                    foreach (var chunk in state.Feed(delta))
                        yield return chunk;
                }
                else if (type is "response.output_text.done" or "response.completed" or "response.failed" or "response.incomplete")
                {
                    foreach (var chunk in state.Flush())
                        yield return chunk;

                    if (type is not "response.output_text.done")
                        yield break;
                }
            }
        }

        foreach (var chunk in state.Flush())
            yield return chunk;
    }

    /// <summary>
    /// Streams the assistant reply as "word-ish" chunks (aligned on whitespace) for a typing-like UX.
    /// Alias used by the UI.
    /// </summary>
    public IAsyncEnumerable<string> StreamAssistantReplyAsync(
        IReadOnlyList<ChatMessageDto> messages,
        CancellationToken ct) =>
        StreamChatWordsAsync(messages, ct);

    /// <summary>
    /// Generates a short title for the current conversation.
    /// Alias used by the UI.
    /// </summary>
    public async Task<string> SuggestTitleAsync(
        IReadOnlyList<ChatMessageDto> messages,
        CancellationToken ct)
    {
        var firstUser = messages.FirstOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content;
        firstUser = (firstUser ?? string.Empty).Trim();
        if (firstUser.Length > 280)
            firstUser = firstUser[..280];

        var titlePrompt = new List<ChatMessageDto>
        {
            new("system", "You generate concise chat titles. Output ONLY the title, 2 to 6 words, no quotes, no punctuation at the end."),
            new("user", $"Conversation starts with: {firstUser}\nTitle:")
        };

        var raw = (await CompleteAsync(titlePrompt, ct).ConfigureAwait(false)).Trim();

        raw = raw.Trim().Trim('"', '\'', '\u201C', '\u201D');
        if (raw.Length > 60)
            raw = raw[..60].Trim();
        return raw;
    }

    private OpenAIResponseRequest CreateRequest(IReadOnlyList<ChatMessageDto> messages, bool stream)
    {
        var apiKey = _opt.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Missing OpenAI API key. Set OpenAI:ApiKey in appsettings.json or define the OPENAI_API_KEY environment variable.");

        _cachedKey = apiKey;

        var input = new List<OpenAIInputMessage>(capacity: messages.Count);
        foreach (var m in messages)
        {
            if (string.IsNullOrWhiteSpace(m.Content))
                continue;

            var role = ChatRoleNormalizer.Normalize(m.Role);
            input.Add(new OpenAIInputMessage(role, m.Content));
        }

        return new OpenAIResponseRequest(
            Model: _opt.Model,
            Input: input,
            Stream: stream,
            Temperature: _opt.Temperature,
            Store: _opt.Store);
    }

    private string? _cachedKey;

    private void ApplyAuth(HttpRequestMessage req)
    {
        var key = _cachedKey;
        if (string.IsNullOrWhiteSpace(key))
            key = _opt.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Missing OpenAI API key. Set OpenAI:ApiKey in appsettings.json or define the OPENAI_API_KEY environment variable.");

        HttpRequestHelper.SetBearerAuth(req, key);
    }
}
