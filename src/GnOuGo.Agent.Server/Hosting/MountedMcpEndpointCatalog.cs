using GnOuGo.Agent.Mcp;
using GnOuGo.DocIngestor.Mcp;
using GnOuGo.KeyVault.Mcp;
using GnOuGo.Mcp.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.Agent.Server.Hosting;

internal static class MountedMcpEndpointCatalog
{
    private static readonly IReadOnlyDictionary<string, MountedMcpEndpointRegistration> RegistrationsByServerName =
        new Dictionary<string, MountedMcpEndpointRegistration>(StringComparer.Ordinal)
        {
            [AgentMcpHostingExtensions.ServerName] = new(
                AgentMcpHostingExtensions.ServerName,
                AgentMcpHostingExtensions.ServerVersion,
                "/mcp/agent",
                "Agent management and chat history via locally mounted MCP HTTP endpoint",
                "GnOuGo.Agent.Server.AgentMcpMount"),
            [KeyVaultMcpHostingExtensions.ServerName] = new(
                KeyVaultMcpHostingExtensions.ServerName,
                KeyVaultMcpHostingExtensions.ServerVersion,
                "/mcp/keyvault",
                "Encrypted secret manager via locally mounted MCP HTTP endpoint",
                "GnOuGo.Agent.Server.KeyVaultMcpMount"),
            [DocsIngestorMcpHostingExtensions.ServerName] = new(
                DocsIngestorMcpHostingExtensions.ServerName,
                DocsIngestorMcpHostingExtensions.ServerVersion,
                "/mcp/docs-ingestor",
                "Document ingestion and vector search via locally mounted MCP HTTP endpoint",
                "GnOuGo.Agent.Server.DocsIngestorMcpMount")
        };

    public static IReadOnlyCollection<MountedMcpEndpointRegistration> Registrations =>
        RegistrationsByServerName.Values.ToArray();

    public static Task ConfigureSessionOptionsAsync(
        HttpContext context,
        McpServerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<MountedMcpEndpointMetadata>()
            ?? throw new InvalidOperationException("The MCP request endpoint is missing its mounted server metadata.");

        if (!RegistrationsByServerName.TryGetValue(metadata.ServerName, out var registration))
            throw new InvalidOperationException($"The MCP server group '{metadata.ServerName}' is not registered.");

        var registeredTools = options.ToolCollection
            ?? throw new InvalidOperationException($"The MCP server group '{registration.ServerName}' has no tool collection.");
        var selectedTools = SelectTools(registeredTools, registration.ServerName);
        if (selectedTools.Count == 0)
            throw new InvalidOperationException($"The MCP server group '{registration.ServerName}' has no registered tools.");

        options.ServerInfo = new Implementation
        {
            Name = registration.ServerName,
            Version = registration.ServerVersion
        };
        options.ToolCollection = selectedTools;

        return Task.CompletedTask;
    }

    public static void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (var registration in Registrations)
        {
            endpoints.MapMcp(registration.RoutePrefix)
                .WithMetadata(new MountedMcpEndpointMetadata(registration.ServerName))
                .DisableAntiforgery();
        }
    }

    internal static McpServerPrimitiveCollection<McpServerTool> SelectTools(
        IEnumerable<McpServerTool> tools,
        string serverName)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        var selected = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
        {
            if (tool.Metadata
                .OfType<McpServerToolGroupAttribute>()
                .Any(group => string.Equals(group.ServerName, serverName, StringComparison.Ordinal)))
            {
                selected.Add(tool);
            }
        }

        return selected;
    }
}

internal sealed record MountedMcpEndpointRegistration(
    string ServerName,
    string ServerVersion,
    string RoutePrefix,
    string DefaultDescription,
    string LoggerName);

internal sealed record MountedMcpEndpointMetadata(string ServerName);
