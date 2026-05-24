using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.Agent.Mcp.Models;

namespace GnOuGo.Agent.Mcp;

internal static class AgentMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, AgentMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(Schedule))]
[JsonSerializable(typeof(Schedule[]))]
[JsonSerializable(typeof(List<Schedule>))]
internal sealed partial class AgentMcpJsonContext : JsonSerializerContext;

