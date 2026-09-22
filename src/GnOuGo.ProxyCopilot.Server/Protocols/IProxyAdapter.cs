using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Configuration;

namespace GnOuGo.ProxyCopilot.Server.Protocols;

public interface IProxyAdapter
{
    string Type { get; }
    string Endpoint(ModelRoute route);
    JsonObject CreateRequest(JsonObject request, ModelRoute route);
    IAsyncEnumerable<JsonObject> ReadResponse(Stream stream, bool streaming, bool includeUsage, ModelRoute route, CancellationToken ct);
}
