using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

internal sealed class StructuredToolAdapter : IProxyAdapter
{
    private readonly IProxyAdapter _native;
    private readonly StructuredToolSelection _selection;
    private readonly string _requestId;

    public StructuredToolAdapter(IProxyAdapter native, JsonObject request, string requestId)
    {
        _native = native;
        _requestId = requestId;
        try { _selection = StructuredToolSelection.FromRequest(request); }
        catch (InvalidDataException ex) { throw new ProxyException(400, "unsupported_parameter", ex.Message); }
    }

    public string Type => _native.Type;
    public string Endpoint(ModelRoute route) => _native.Endpoint(route);

    public JsonObject CreateRequest(JsonObject request, ModelRoute route)
    {
        var body = _native.CreateRequest(request, route);
        body["messages"] = StructuredToolSelection.CreateMessages(request);
        body.Remove("tools");
        body.Remove("tool_choice");
        body.Remove("parallel_tool_calls");
        body.Remove("stream_options");
        body["stream"] = false;
        body["response_format"] = new JsonObject {
            ["type"] = "json_schema", ["json_schema"] = new JsonObject {
                ["name"] = "output", ["strict"] = true, ["schema"] = _selection.CreateSchema()
            }
        };
        return body;
    }

    public async IAsyncEnumerable<JsonObject> ReadResponse(Stream stream, bool streaming, bool includeUsage,
        ModelRoute route, [EnumeratorCancellation] CancellationToken ct)
    {
        var completion = await WireReader.ObjectAsync(stream, ct);
        completion["model"] = route.Id;
        // Validate the complete decision before the first potentially executable delta reaches the client.
        foreach (var chunk in _selection.Restore(completion, streaming, _requestId))
            yield return chunk;
    }
}
