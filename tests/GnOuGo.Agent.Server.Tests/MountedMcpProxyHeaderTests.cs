using GnOuGo.Agent.Server.Hosting;

namespace GnOuGo.Agent.Server.Tests;

public sealed class MountedMcpProxyHeaderTests
{
    [Theory]
    [InlineData("MCP-Protocol-Version")]
    [InlineData("Mcp-Method")]
    [InlineData("Mcp-Name")]
    [InlineData("Mcp-Param-Cursor")]
    [InlineData("Mcp-Session-Id")]
    public void StandardMcpHeaders_AreForwarded(string headerName)
    {
        Assert.False(GnOuGoAgentWebHost.ShouldSkipProxyRequestHeader(headerName, hasBody: true));
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Host")]
    [InlineData("Transfer-Encoding")]
    public void HopByHopHeaders_AreNotForwarded(string headerName)
    {
        Assert.True(GnOuGoAgentWebHost.ShouldSkipProxyRequestHeader(headerName, hasBody: true));
    }

    [Theory]
    [InlineData("Content-Length")]
    [InlineData("Content-Type")]
    public void BodyHeaders_AreRebuiltByTheProxy(string headerName)
    {
        Assert.True(GnOuGoAgentWebHost.ShouldSkipProxyRequestHeader(headerName, hasBody: true));
        Assert.False(GnOuGoAgentWebHost.ShouldSkipProxyRequestHeader(headerName, hasBody: false));
    }
}
