namespace GnOuGo.Mcp.Core.Tests;

public sealed class GnOuGoMcpProtocolTests
{
    [Fact]
    public void OwnedPeers_PreferStableJuly2026Revision()
    {
        Assert.Equal("2026-07-28", GnOuGoMcpProtocol.PreferredRevision);
    }

    [Fact]
    public void RequiredRevision_RemainsCompatibleAlias()
    {
#pragma warning disable CS0618
        Assert.Equal(GnOuGoMcpProtocol.PreferredRevision, GnOuGoMcpProtocol.RequiredRevision);
#pragma warning restore CS0618
    }

    [Fact]
    public void ExternalFallback_IsStableNovember2025Revision()
    {
        Assert.Equal("2025-11-25", GnOuGoMcpProtocol.LegacyFallbackRevision);
    }
}
