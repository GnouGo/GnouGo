namespace GnOuGo.Mcp.Core;

/// <summary>
/// Stable MCP revisions used by GnOuGo-owned peers. External clients leave the
/// version unpinned so the SDK can discover 2026-07-28 and fall back to 2025-11-25.
/// </summary>
public static class GnOuGoMcpProtocol
{
    public const string RequiredRevision = "2026-07-28";
    public const string LegacyFallbackRevision = "2025-11-25";
}
