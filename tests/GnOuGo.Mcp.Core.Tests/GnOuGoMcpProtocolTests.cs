namespace GnOuGo.Mcp.Core.Tests;

public sealed class GnOuGoMcpProtocolTests
{
    [Fact]
    public void OwnedPeers_RequireStableJuly2026Revision()
    {
        Assert.Equal("2026-07-28", GnOuGoMcpProtocol.RequiredRevision);
    }

    [Fact]
    public void ExternalFallback_IsStableNovember2025Revision()
    {
        Assert.Equal("2025-11-25", GnOuGoMcpProtocol.LegacyFallbackRevision);
    }
}
