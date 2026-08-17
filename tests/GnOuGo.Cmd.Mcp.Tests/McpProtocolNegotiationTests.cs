using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public sealed class McpProtocolNegotiationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task LiveStdioDiscovery_SupportsAutomaticAndLegacyNegotiation(string? protocolVersion)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "GnOuGo.Cmd.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        Assert.True(File.Exists(executable), $"The MCP test executable was not found at '{executable}'.");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            Name = "GnOuGo.Cmd.Mcp.Tests",
            WorkingDirectory = AppContext.BaseDirectory
        });

        await using var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ProtocolVersion = protocolVersion,
                ClientInfo = new Implementation
                {
                    Name = "GnOuGo.Cmd.Mcp.Tests",
                    Version = "1.0.0"
                }
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(protocolVersion ?? "2026-07-28", client.NegotiatedProtocolVersion);
        Assert.Contains(tools, tool => tool.Name == "cmd_list_allowed_commands");
        Assert.Contains(tools, tool => tool.Name == "cmd_get_policy");
        var cmdRun = Assert.Single(tools, tool => tool.Name == "cmd_run");
        var allowedCommands = cmdRun.JsonSchema.GetProperty("properties")
            .GetProperty("commandName")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        Assert.Contains("delete_directory", allowedCommands);
    }
}
