using Microsoft.Extensions.DependencyInjection;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;

namespace GnOuGo.Agent.Server.Tests;

internal static class AgentMcpTestPersistence
{
    public static string CreateIsolatedDatabasePath(string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);

        var workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "gnougo-agent-server-tests",
            $"{scenario}-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(workspaceRoot, ".GnOuGo", "data");
        Directory.CreateDirectory(dataDirectory);
        return Path.Combine(dataDirectory, "agent.db");
    }

    public static void CleanupIsolatedWorkspace(string dbPath)
    {
        var dataDirectory = Directory.GetParent(Path.GetFullPath(dbPath));
        var metadataDirectory = dataDirectory?.Parent;
        var workspaceRoot = metadataDirectory?.Parent;
        var expectedTestRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "gnougo-agent-server-tests"));

        if (dataDirectory is null
            || metadataDirectory is null
            || workspaceRoot is null
            || !string.Equals(dataDirectory.Name, "data", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadataDirectory.Name, ".GnOuGo", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(workspaceRoot.Parent?.FullName, expectedTestRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete an unexpected test workspace for database '{dbPath}'.");
        }

        try
        {
            if (workspaceRoot.Exists)
                workspaceRoot.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static async Task SeedAgentAsync(string dbPath, string name, string workflow, CancellationToken ct = default)
        => await WithAgentMcpServicesAsync(dbPath, async scope =>
        {
            var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
            await agents.AddAgentAsync(name, workflow, ct: ct);
        }, ct);

    public static async Task SeedUserConfigAsync(string dbPath, UserConfigUpdate update, CancellationToken ct = default)
        => await WithAgentMcpServicesAsync(dbPath, async scope =>
        {
            var userConfigs = scope.ServiceProvider.GetRequiredService<IUserConfigRepository>();
            await userConfigs.SetAsync(update, ct: ct);
        }, ct);

    public static async Task<UserConfigSnapshot> GetUserConfigAsync(string dbPath, CancellationToken ct = default)
    {
        UserConfigSnapshot? snapshot = null;
        await WithAgentMcpServicesAsync(dbPath, async scope =>
        {
            var userConfigs = scope.ServiceProvider.GetRequiredService<IUserConfigRepository>();
            snapshot = await userConfigs.GetAsync(ct: ct);
        }, ct);
        return snapshot!;
    }

    public static async Task<AgentDefinition?> GetAgentByNameAsync(string dbPath, string name, CancellationToken ct = default)
    {
        AgentDefinition? agent = null;
        await WithAgentMcpServicesAsync(dbPath, async scope =>
        {
            var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
            agent = await agents.GetByNameAsync(name, ct);
        }, ct);
        return agent;
    }

    private static async Task WithAgentMcpServicesAsync(
        string dbPath,
        Func<AsyncServiceScope, Task> action,
        CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddAgentMcpPersistence(dbPath);

        await using var provider = services.BuildServiceProvider();
        await provider.InitializeAgentMcpAsync(ct);

        await using var scope = provider.CreateAsyncScope();
        await action(scope);
    }
}
