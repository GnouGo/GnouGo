using System.Text.Json;
using System.Text.Json.Serialization;

namespace GnOuGo.Cmd.Mcp;

/// <summary>
/// Provides pre-configured <see cref="JsonSerializerOptions"/> for Native AOT serialization
/// of MCP tool return types. Required because JsonSerializerIsReflectionEnabledByDefault is disabled.
/// </summary>
internal static class CmdMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, CmdMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(CmdAllowedCommandsResult))]
[JsonSerializable(typeof(CmdAllowedCommandInfo))]
[JsonSerializable(typeof(IReadOnlyList<CmdAllowedCommandInfo>))]
[JsonSerializable(typeof(CmdPolicyInfo))]
[JsonSerializable(typeof(CmdEnvironmentInfo))]
[JsonSerializable(typeof(CmdShellAvailability))]
[JsonSerializable(typeof(IReadOnlyList<CmdShellAvailability>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(CmdRunResult))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CmdMcpJsonContext : JsonSerializerContext;


