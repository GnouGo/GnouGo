using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.ProxyCopilot.Server.Traffic;

namespace GnOuGo.ProxyCopilot.Server;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(TrafficSnapshot))]
[JsonSerializable(typeof(TrafficDetail))]
public partial class ProxyJsonContext : JsonSerializerContext;
