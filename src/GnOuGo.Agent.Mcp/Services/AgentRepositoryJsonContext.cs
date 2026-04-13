using System.Collections.Generic;
using System.Text.Json.Serialization;
using GnOuGo.Agent.Mcp.Models;

namespace GnOuGo.Agent.Mcp.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(List<Schedule>))]
internal partial class AgentRepositoryJsonContext : JsonSerializerContext
{
}
