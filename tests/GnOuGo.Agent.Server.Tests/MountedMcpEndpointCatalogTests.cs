using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.KeyVault.Mcp;
using GnOuGo.Mcp.Core;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.Agent.Server.Tests;

public sealed class MountedMcpEndpointCatalogTests
{
    [Fact]
    public void SelectTools_ReturnsOnlyToolsMarkedForTheRequestedServer()
    {
        var selected = MountedMcpEndpointCatalog.SelectTools(
            [CreateTool<AgentTestTools>(nameof(AgentTestTools.Agent)),
             CreateTool<KeyVaultTestTools>(nameof(KeyVaultTestTools.KeyVault)),
             CreateTool<UngroupedTestTools>(nameof(UngroupedTestTools.Ungrouped))],
            AgentMcpHostingExtensions.ServerName);

        var tool = Assert.Single(selected);
        Assert.Equal("test_agent", tool.ProtocolTool.Name);
    }

    [Fact]
    public async Task ConfigureSessionOptions_SetsExactIdentityAndRequestLocalToolCollection()
    {
        var context = CreateContext(new MountedMcpEndpointMetadata(AgentMcpHostingExtensions.ServerName));
        var options = CreateOptions(
            CreateTool<AgentTestTools>(nameof(AgentTestTools.Agent)),
            CreateTool<KeyVaultTestTools>(nameof(KeyVaultTestTools.KeyVault)));

        await MountedMcpEndpointCatalog.ConfigureSessionOptionsAsync(context, options, CancellationToken.None);

        var serverInfo = Assert.IsType<Implementation>(options.ServerInfo);
        Assert.Equal(AgentMcpHostingExtensions.ServerName, serverInfo.Name);
        Assert.Equal(AgentMcpHostingExtensions.ServerVersion, serverInfo.Version);
        var tool = Assert.Single(options.ToolCollection!);
        Assert.Equal("test_agent", tool.ProtocolTool.Name);
    }

    [Fact]
    public async Task ConfigureSessionOptions_FailsClosedWithoutEndpointMetadata()
    {
        var options = CreateOptions(CreateTool<AgentTestTools>(nameof(AgentTestTools.Agent)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MountedMcpEndpointCatalog.ConfigureSessionOptionsAsync(
                CreateContext(), options, CancellationToken.None));

        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigureSessionOptions_FailsClosedForUnknownGroup()
    {
        var options = CreateOptions(CreateTool<AgentTestTools>(nameof(AgentTestTools.Agent)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MountedMcpEndpointCatalog.ConfigureSessionOptionsAsync(
                CreateContext(new MountedMcpEndpointMetadata("GnOuGo.Unknown.Mcp")),
                options,
                CancellationToken.None));

        Assert.Contains("not registered", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DefaultHttpContext CreateContext(params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            static _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "test MCP endpoint"));
        return context;
    }

    private static McpServerOptions CreateOptions(params McpServerTool[] tools)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
            collection.Add(tool);

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "placeholder", Version = "0.0.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            ToolCollection = collection
        };
    }

    private static McpServerTool CreateTool<T>(string methodName)
        => McpServerTool.Create(typeof(T).GetMethod(methodName)!, target: null, options: null);

    [McpServerToolGroup(AgentMcpHostingExtensions.ServerName)]
    private sealed class AgentTestTools
    {
        [McpServerTool(Name = "test_agent")]
        public static string Agent() => "agent";
    }

    [McpServerToolGroup(KeyVaultMcpHostingExtensions.ServerName)]
    private sealed class KeyVaultTestTools
    {
        [McpServerTool(Name = "test_keyvault")]
        public static string KeyVault() => "keyvault";
    }

    private sealed class UngroupedTestTools
    {
        [McpServerTool(Name = "test_ungrouped")]
        public static string Ungrouped() => "ungrouped";
    }
}
