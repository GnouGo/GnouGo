using System.Text.Json;
using System.Text.Json.Serialization;

namespace GnOuGo.Cmd.Mcp;

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
[JsonSerializable(typeof(CmdPolicyInfo))]
[JsonSerializable(typeof(CmdEnvironmentInfo))]
[JsonSerializable(typeof(CmdShellAvailability))]
[JsonSerializable(typeof(CmdAllowedCommandInfo))]
[JsonSerializable(typeof(CmdRunResult))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<CmdShellAvailability>))]
[JsonSerializable(typeof(IReadOnlyList<CmdAllowedCommandInfo>))]
internal sealed partial class CmdMcpJsonContext : JsonSerializerContext;

