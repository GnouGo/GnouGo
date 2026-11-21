using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Mcp;

// An explicit protocol adaptation, never an error-triggered fallback or a tool executor.
internal sealed class CopilotStructuredToolAdapter : CopilotInferenceAdapter
{
    internal const string TransportName = StructuredToolSelection.TransportName;

    internal static string? NormalizeTransport(string? value) => value switch
    {
        null or "native" => null,
        TransportName => TransportName,
        _ => throw new InvalidDataException("Unsupported Copilot inference transport.")
    };

    public override Task<CopilotPreparedInference> PrepareAsync(HttpRequestMessage request,
        GitHub.Copilot.CopilotRequestContext context)
        => PrepareAsync(request, context.RequestId, context.CancellationToken);

    internal async Task<CopilotPreparedInference> PrepareAsync(HttpRequestMessage request, string requestId, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri is not { IsAbsoluteUri: true } endpoint ||
            !endpoint.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) || endpoint.UserInfo.Length != 0 || request.Content is null)
            throw new InvalidDataException("The structured tool transport requires the configured Chat Completions request.");
        var original = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))?.AsObject()
            ?? throw new InvalidDataException("Missing SDK inference body.");
        var wire = original.DeepClone().AsObject();
        StructuredToolSelection? selection = null;
        if (original["tools"] is JsonArray { Count: > 0 })
        {
            selection = StructuredToolSelection.FromRequest(original);
            var model = original["model"]?.GetValue<string>() ?? throw new InvalidDataException("Missing SDK model.");
            var output = (original["max_completion_tokens"] ?? original["max_tokens"])?.GetValue<int>() ?? 8192;
            wire = JsonNode.Parse(ChatRequestBuilder.OpenAiFull(model, "", structuredOutputSchema: selection.CreateSchema(),
                structuredOutputStrict: true, reasoning: original["reasoning_effort"]?.GetValue<string>(), maxOutputTokens: output))!.AsObject();
            foreach (var pair in original)
                if (pair.Key is not ("model" or "messages" or "tools" or "tool_choice" or "parallel_tool_calls" or "response_format" or
                    "stream" or "stream_options" or "reasoning_effort" or "max_tokens" or "max_completion_tokens"))
                    wire[pair.Key] = pair.Value?.DeepClone();
            wire["messages"] = StructuredToolSelection.CreateMessages(original);
            // Fully validate a model decision before the SDK can execute it. SDK streaming
            // is restored below; the upstream remains one metered GNouGo JSON request.
            wire["stream"] = false;
        }
        var actual = new HttpRequestMessage(request.Method, endpoint)
        {
            Version = request.Version, VersionPolicy = request.VersionPolicy,
            Content = new StringContent(wire.ToJsonString(), Encoding.UTF8, "application/json")
        };
        foreach (var header in request.Headers) actual.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (selection is not null)
        {
            actual.Headers.Accept.Clear(); actual.Headers.Accept.ParseAdd("application/json");
        }
        return new Prepared(actual, selection, original["stream"]?.GetValue<bool>() == true, requestId);
    }

    private sealed class Prepared(HttpRequestMessage request, StructuredToolSelection? selection,
        bool streaming, string requestId) : CopilotPreparedInference
    {
        public override HttpRequestMessage Request => request;
        public override bool TryRequireFinalResponse(JsonObject body)
        {
            if (selection?.RestrictToFinalResponse() is not { } final ||
                body["messages"] is not JsonArray messages || !messages.Any(m => m?["role"]?.ToString() == "tool"))
                return false;
            body["response_format"]!["json_schema"]!["schema"] = final.CreateSchema();
            selection = final;
            return true;
        }

        public override async Task<HttpResponseMessage> RestoreResponseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if (selection is null || !response.IsSuccessStatusCode) return response;
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject()
                ?? throw new InvalidDataException("The provider returned no structured decision.");
            var chunks = selection.Restore(root, streaming, requestId);
            var body = streaming
                ? string.Concat(chunks.Select(chunk => "data: " + chunk.ToJsonString() + "\n\n")) + "data: [DONE]\n\n"
                : chunks.Single().ToJsonString();
            var contentType = streaming ? "text/event-stream" : "application/json";
            var restored = new HttpResponseMessage(response.StatusCode) { Version = response.Version,
                ReasonPhrase = response.ReasonPhrase, Content = new StringContent(body, Encoding.UTF8, contentType) };
            foreach (var header in response.Headers) restored.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return restored;
        }
    }
}
