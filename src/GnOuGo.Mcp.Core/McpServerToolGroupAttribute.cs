namespace GnOuGo.Mcp.Core;

/// <summary>
/// Associates an MCP tool type with the logical server that should expose it when
/// several independently packaged tool catalogs share one transport host.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class McpServerToolGroupAttribute : Attribute
{
    public McpServerToolGroupAttribute(string serverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ServerName = serverName;
    }

    public string ServerName { get; }
}
