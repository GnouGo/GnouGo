using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GnOuGo.Cmd.Mcp.Tests;

public sealed class McpProtocolNegotiationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gnougo-cmd-protocol-").FullName;

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task LiveStdioDiscovery_SupportsAutomaticAndLegacyNegotiation(string? protocolVersion)
    {
        var executable = Environment.GetEnvironmentVariable("GNOU_GO_CMD_MCP_TEST_EXECUTABLE") ?? Path.Combine(
            AppContext.BaseDirectory,
            "GnOuGo.Cmd.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        Assert.True(File.Exists(executable), $"The MCP test executable was not found at '{executable}'.");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            Name = "GnOuGo.Cmd.Mcp.Tests",
            WorkingDirectory = Path.GetDirectoryName(executable),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["Cmd__DefaultWorkingDirectory"] = _root,
                ["OpenTelemetry__Enabled"] = "false"
            }
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
        Assert.Equal(PackagedFilesystemTests.ExpectedCommands, allowedCommands);

        async Task Run(string command, JsonObject parameters)
        {
            var result = await client.CallToolAsync("cmd_run", new Dictionary<string, object?>
            {
                ["commandName"] = command, ["parameters"] = parameters.DeepClone()
            }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(result.StructuredContent);
            var value = JsonNode.Parse(result.StructuredContent.ToString()!)!;
            Assert.True(result.IsError != true, value.ToJsonString());
            Assert.True(value["success"]!.GetValue<bool>()); Assert.Equal(0, value["exitCode"]!.GetValue<int>());
        }

        await Run("delete_directory", new() { ["path"] = "fresh" });
        await Run("create_directory", new() { ["path"] = "source" });
        await Run("write_file", new() { ["path"] = "source/note.txt", ["contentBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("é東京\nactual MCP output")) });
        await Run("copy_file", new() { ["source"] = "source/note.txt", ["destination"] = "source/copied.txt" });
        await Run("move_file", new() { ["source"] = "source/copied.txt", ["destination"] = "source/renamed.txt" });
        await Run("copy_directory", new() { ["source"] = "source", ["destination"] = "copied" });
        await Run("move_directory", new() { ["source"] = "copied", ["destination"] = "renamed" });
        Assert.Equal("é東京\nactual MCP output", File.ReadAllText(Path.Combine(_root, "renamed", "renamed.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "copied")));
        Assert.False(File.Exists(Path.Combine(_root, "source", "copied.txt")));
        await Run("delete_directory", new() { ["path"] = "renamed" });
        await Run("delete_directory", new() { ["path"] = "renamed" });
        Assert.False(Directory.Exists(Path.Combine(_root, "renamed")));
    }

    [Fact]
    public async Task AllowlistedCommandsCannotReadTheMcpTransport()
    {
        var executable = Environment.GetEnvironmentVariable("GNOU_GO_CMD_MCP_TEST_EXECUTABLE") ?? Path.Combine(
            AppContext.BaseDirectory, "GnOuGo.Cmd.Mcp" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = executable,
            WorkingDirectory = Path.GetDirectoryName(executable),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["Cmd__DefaultWorkingDirectory"] = _root,
                ["OpenTelemetry__Enabled"] = "false",
                ["Cmd__AllowedCommands__stdin_fixture__Shell"] = OperatingSystem.IsWindows() ? "powershell" : "sh",
                ["Cmd__AllowedCommands__stdin_fixture__Script"] = OperatingSystem.IsWindows()
                    ? "if ([Console]::ReadLine() -ne $null) { throw 'Unexpected inherited input.' }; Write-Output 'stdin closed'"
                    : "if IFS= read -r inherited; then printf '%s' 'Unexpected inherited input' >&2; exit 1; fi; printf '%s' 'stdin closed'"
            }
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("cmd_run", new Dictionary<string, object?>
        {
            ["commandName"] = "stdin_fixture", ["timeoutMs"] = 5000
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result.StructuredContent);
        var value = JsonNode.Parse(result.StructuredContent.ToString()!)!;
        Assert.True(result.IsError != true, value.ToJsonString());
        Assert.True(value["success"]!.GetValue<bool>());
        Assert.False(value["timedOut"]!.GetValue<bool>());
        Assert.Equal("stdin closed", value["stdout"]!.GetValue<string>().Trim());
        // The MCP input stream is still usable after the child process exits.
        Assert.NotEmpty(await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
