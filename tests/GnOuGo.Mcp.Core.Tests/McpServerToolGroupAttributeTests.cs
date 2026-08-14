using GnOuGo.Mcp.Core;

namespace GnOuGo.Mcp.Core.Tests;

public sealed class McpServerToolGroupAttributeTests
{
    [Fact]
    public void Constructor_PreservesServerName()
    {
        var attribute = new McpServerToolGroupAttribute("GnOuGo.Test.Mcp");

        Assert.Equal("GnOuGo.Test.Mcp", attribute.ServerName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankServerName(string serverName)
    {
        Assert.Throws<ArgumentException>(() => new McpServerToolGroupAttribute(serverName));
    }
}
