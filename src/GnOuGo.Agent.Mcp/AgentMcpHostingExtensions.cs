using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using GnOuGo.Mcp.Core;
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
    public const string DefaultDatabasePath = ".GnOuGo/data/gnougo-agent.db";

    public static string ResolveDatabasePath(string? configuredPath, string baseDirectory)
        => GnOuGoWorkspace.ResolveDatabasePath(configuredPath, baseDirectory, DefaultDatabasePath);

    [UnconditionalSuppressMessage("AOT", "IL2026",
        Justification = "EF Core with SQLite uses TrimMode=partial to preserve required assemblies.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "EF Core with SQLite uses TrimMode=partial to preserve required assemblies.")]
    public static IServiceCollection AddAgentMcpPersistence(this IServiceCollection services, string databasePath, string? agentsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = $"Data Source={databasePath}";

        services.AddDbContext<AgentMcpDbContext>(options => options.UseSqlite(connectionString));
        services.AddDbContext<DiffDbContext>(options => options.UseDiffCoreSqlite(connectionString));
        services.AddScoped<DiffService>();
        var resolvedAgentsDirectory = ResolveAgentsDirectory(databasePath, agentsDirectory);
        services.AddSingleton<IAgentRepository>(_ => new AgentRepository(resolvedAgentsDirectory));
        services.AddScoped<IUserConfigRepository, UserConfigRepository>();
        services.AddSingleton<InMemoryChatHistoryStore>();
        return services;
    }

    public static string ResolveAgentsDirectory(string databasePath, string? configuredAgentsDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredAgentsDirectory))
        {
            var configured = configuredAgentsDirectory.Trim();
            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(Path.Combine(ResolveWorkspaceRootFromDatabasePath(databasePath), configured));
        }

        return Path.Combine(
            ResolveWorkspaceRootFromDatabasePath(databasePath),
            GnOuGoWorkspace.WorkspaceDataSubfolder);
    }

    private static string ResolveWorkspaceRootFromDatabasePath(string databasePath)
    {
        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (string.IsNullOrWhiteSpace(databaseDirectory))
            return GnOuGoWorkspace.ResolveDefaultWorkingDirectorySafe(contentRootPath: AppContext.BaseDirectory);

        var directory = new DirectoryInfo(databaseDirectory);
        if (string.Equals(directory.Name, "data", StringComparison.OrdinalIgnoreCase) && directory.Parent is not null)
        {
            if (string.Equals(directory.Parent.Name, GnOuGoWorkspace.WorkspaceDataSubfolder, StringComparison.OrdinalIgnoreCase)
                && directory.Parent.Parent is not null)
            {
                return directory.Parent.Parent.FullName;
            }

            return directory.Parent.FullName;
        }

        return directory.FullName;
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
                options.AddGnOuGoToolErrorNormalizer();
            })
            .WithHttpTransport()
            .WithTools<DataTools>(AgentMcpJson.SerializerOptions)
            .WithTools<AgentTools>(AgentMcpJson.SerializerOptions);

        return services;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "EnsureCreatedAsync is used at startup to bootstrap the SQLite schema.")]
    public static async Task InitializeAgentMcpAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMcpDbContext>();
        await AgentMcpDatabaseBootstrap.EnsureCreatedAsync(db, ct);

        var diffDb = scope.ServiceProvider.GetRequiredService<DiffDbContext>();
        await diffDb.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "DiffEntries" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_DiffEntries" PRIMARY KEY,
                "EntityType" TEXT NOT NULL,
                "EntityId" TEXT NOT NULL,
                "Author" TEXT NOT NULL,
                "ValueHash" TEXT NOT NULL,
                "CurrentValue" TEXT NOT NULL,
                "DiffFromPrevious" TEXT,
                "TimestampTicks" INTEGER NOT NULL
            )
            """, ct);
        await diffDb.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_DiffEntries_EntityType_EntityId_TimestampTicks" ON "DiffEntries" ("EntityType", "EntityId", "TimestampTicks")""", ct);
        await diffDb.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_DiffEntries_EntityType_EntityId" ON "DiffEntries" ("EntityType", "EntityId")""", ct);
        await diffDb.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_DiffEntries_TimestampTicks" ON "DiffEntries" ("TimestampTicks")""", ct);
    }

    public static IEndpointConventionBuilder MapAgentMcp(this IEndpointRouteBuilder endpoints, string pattern = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapMcp(pattern)
            .DisableAntiforgery();
    }
}
