using ModelContextProtocol.Protocol;
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

    public static IServiceCollection AddAgentMcpPersistence(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddSingleton(new AgentSqliteStore(databasePath));
        services.AddDbContext<DiffDbContext>(options => options.UseDiffCoreSqlite($"Data Source={databasePath}"));
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
        var store = scope.ServiceProvider.GetRequiredService<AgentSqliteStore>();

        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(store, ct);
    }

    public static IEndpointConventionBuilder MapAgentMcp(this IEndpointRouteBuilder endpoints, string pattern = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapMcp(pattern)
            .DisableAntiforgery();
    }
}

