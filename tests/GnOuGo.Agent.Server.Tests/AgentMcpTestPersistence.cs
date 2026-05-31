using Microsoft.Extensions.DependencyInjection;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;

namespace GnOuGo.Agent.Server.Tests;

internal static class AgentMcpTestPersistence
{
    public static async Task SeedAgentAsync(string dbPath, string name, string workflow, CancellationToken ct = default)
        => await WithAgentMcpServicesAsync(dbPath, async scope =>
        {
            var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
            await agents.AddAgentAsync(name, workflow, new List<Schedule>(), ct: ct);
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

