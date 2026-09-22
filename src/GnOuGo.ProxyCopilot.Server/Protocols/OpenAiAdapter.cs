using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

public sealed class OpenAiAdapter(string type = "openai") : IProxyAdapter
{
    public string Type => type;
    public string Endpoint(ModelRoute route) => type == "copilot"
        ? CopilotEndpoints.ChatCompletions(route.Options.Connection.Url)
        : OpenAiEndpoints.ChatCompletions(route.Options.Connection.Url, route.Options.Connection.ApiVersion);

    public JsonObject CreateRequest(JsonObject request, ModelRoute route)
    {
        var body = (JsonObject)request.DeepClone();
        body["model"] = route.Model.UpstreamId;
        // Normalize client aliases before forwarding, including explicit null fields.
        body.Remove("max_tokens");
        body.Remove("max_completion_tokens");
        if (ChatContract.OutputLimit(request, route) is { } limit)
        {
            var unsupported = route.Model.Metadata.Capabilities.UnsupportedRequestParameters;
            var field = unsupported?.Contains("max_completion_tokens", StringComparer.Ordinal) == true
                ? "max_tokens" : "max_completion_tokens";
            if (unsupported?.Contains(field, StringComparer.Ordinal) == true) throw ChatContract.Unsupported(field);
            body[field] = limit;
        }
        return body;
    }

    public async IAsyncEnumerable<JsonObject> ReadResponse(Stream stream, bool streaming, bool includeUsage, ModelRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!streaming)
        {
            var response = await WireReader.ObjectAsync(stream, ct);
            if (response["error"] is not null || response["choices"] is not JsonArray { Count: > 0 } choices
                || choices.Any(choice => choice?["message"] is not JsonObject || choice["finish_reason"] is null))
                throw WireReader.Invalid("Upstream returned no complete chat message.");
            response["model"] = route.Id;
            yield return response;
            yield break;
        }
        var finished = false;
        var done = false;
        await foreach (var data in WireReader.Sse(stream, ct))
        {
            if (data == "[DONE]") { done = true; break; }
            var chunk = WireReader.Object(data);
            if (chunk["error"] is not null) throw WireReader.Invalid("Upstream reported a streaming error.");
            if (chunk["choices"] is not JsonArray choices) throw WireReader.Invalid("Upstream chunk has no choices.");
            finished |= choices.Any(c => c?["finish_reason"] is not null);
            chunk["model"] = route.Id;
            yield return chunk;
        }
        if (!done || !finished) throw WireReader.Invalid("Upstream stream ended before completion.");
    }
}
