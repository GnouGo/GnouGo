using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace GnOuGo.KeyVault.Mcp;

public static class KeyVaultMcpHostingExtensions
{
    public const string ServerName = "GnOuGo.KeyVault.Mcp";
    public const string ServerVersion = "1.0.0";
    public const string DefaultRoutePrefix = "/mcp";

    public static IServiceCollection AddKeyVaultMcpPersistence(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddSingleton(new KeyVaultSqliteStore(databasePath));
        return services;
    }

    public static IServiceCollection AddKeyVaultMcpHttpServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTransient<KeyVaultTools>();
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
            .WithTools<KeyVaultTools>(KeyVaultMcpJson.SerializerOptions);

        return services;
    }

    public static async Task InitializeKeyVaultMcpAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var keyVault = services.GetRequiredService<KeyVaultSqliteStore>();
        await keyVault.InitializeAsync(ct);
    }

    public static IEndpointConventionBuilder MapKeyVaultMcp(this IEndpointRouteBuilder endpoints, string pattern = DefaultRoutePrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapMcp(pattern)
            .DisableAntiforgery();
    }
}

