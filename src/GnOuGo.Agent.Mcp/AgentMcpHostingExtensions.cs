using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Services;
using GnOuGo.Workspace;

namespace GnOuGo.Agent.Mcp;

public static class AgentMcpHostingExtensions
{
    public const string ServerName = "GnOuGo.Agent.Mcp";
    public const string ServerVersion = "1.0.0";
    public const string DefaultRoutePrefix = "/mcp";
    public const string DefaultDatabasePath = "data/gnougo-agent.db";

    public static string ResolveDatabasePath(string? configuredPath, string baseDirectory)
        => GnOuGoWorkspace.ResolveDatabasePath(configuredPath, baseDirectory, DefaultDatabasePath);

    [UnconditionalSuppressMessage("AOT", "IL2026",
        Justification = "EF Core with SQLite uses TrimMode=partial to preserve required assemblies.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "EF Core with SQLite uses TrimMode=partial to preserve required assemblies.")]
    public static IServiceCollection AddAgentMcpPersistence(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = $"Data Source={databasePath}";

        services.AddDbContext<AgentMcpDbContext>(options => options.UseSqlite(connectionString));
        services.AddDbContext<DiffDbContext>(options => options.UseDiffCoreSqlite(connectionString));
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
            .WithTools<DataTools>(AgentMcpJson.SerializerOptions)
            .WithTools<AgentTools>(AgentMcpJson.SerializerOptions);

        return services;
    }

    public static async Task InitializeAgentMcpAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMcpDbContext>();
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(db, ct);
    }

    public static IEndpointConventionBuilder MapAgentMcp(this IEndpointRouteBuilder endpoints, string pattern = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapMcp(pattern)
            .DisableAntiforgery();
    }
}
