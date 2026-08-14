namespace GnOuGo.Mcp.Core;

/// <summary>
/// Stable MCP revisions preferred and supported by GnOuGo-owned peers. Clients
/// and servers leave the configured version unpinned so the SDK can discover
/// 2026-07-28 and fall back to 2025-11-25.
/// </summary>
public static class GnOuGoMcpProtocol
{
    public const string PreferredRevision = "2026-07-28";

    [Obsolete($"Use {nameof(PreferredRevision)}. GnOuGo servers negotiate supported revisions automatically.")]
    public const string RequiredRevision = PreferredRevision;

    public const string LegacyFallbackRevision = "2025-11-25";
}
