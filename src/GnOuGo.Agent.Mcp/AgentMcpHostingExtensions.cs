using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Services;

namespace GnOuGo.Agent.Mcp;

public static class AgentMcpHostingExtensions
{
    public const string ServerName = "GnOuGo.Agent.Mcp";
    public const string ServerVersion = "1.0.0";
    public const string DefaultRoutePrefix = "/mcp";
    public const string DefaultDatabasePath = "data/gnougo-agent.db";

    public static string ResolveDatabasePath(string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
            return configuredPath;

        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultDatabasePath
            : configuredPath.Replace('\\', '/').Trim();

        if (string.Equals(normalized, DefaultDatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(
                ResolveDesktopDirectory(),
                "GnOuGo",
                "data",
                "gnougo-agent.db");
        }

        return Path.Combine(baseDirectory, configuredPath!);
    }

    public static IServiceCollection AddAgentMcpPersistence(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddDbContext<AgentDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));
        services.AddDbContext<DiffDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<DiffService>();
        services.AddScoped<IAgentRepository, AgentRepository>();
        services.AddScoped<IUserConfigRepository, UserConfigRepository>();
        services.AddSingleton<InMemoryChatHistoryStore>();
        return services;
    }

    public static IServiceCollection AddAgentMcpHttpServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTransient<DataTools>();
        services.AddTransient<AgentTools>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Version = ServerVersion
                };
            })
            .WithHttpTransport()
            .WithTools<DataTools>()
            .WithTools<AgentTools>();

        return services;
    }

    public static async Task InitializeAgentMcpAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var agentDb = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var diffDb = scope.ServiceProvider.GetRequiredService<DiffDbContext>();

        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(agentDb, diffDb, ct);
    }

    public static IEndpointConventionBuilder MapAgentMcp(this IEndpointRouteBuilder endpoints, string pattern = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapMcp(pattern)
            .DisableAntiforgery();
    }

    private static string ResolveDesktopDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath))
            return Path.GetFullPath(desktopPath);

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfilePath))
            return Path.GetFullPath(Path.Combine(userProfilePath, "Desktop"));

        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homePath))
            return Path.GetFullPath(Path.Combine(homePath, "Desktop"));

        throw new InvalidOperationException("Unable to resolve the current user's Desktop directory.");
    }
}

