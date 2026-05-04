using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using GnOuGo.KeyVault.Core;
using GnOuGo.Observability.Core;

namespace GnOuGo.KeyVault.Mcp;

public static class KeyVaultMcpWebHost
{
    public static WebApplication Build(string[] args, string? urls = null, string routePrefix = KeyVaultMcpHostingExtensions.DefaultRoutePrefix)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.AddGnOuGoOpenTelemetry("GnOuGo.KeyVault.Mcp");

        var dbRelativePath = builder.Configuration.GetValue<string>("KeyVault:DatabasePath")
            ?? KeyVaultDatabasePathResolver.DefaultRelativePath;
        var dbPath = KeyVaultDatabasePathResolver.Resolve(dbRelativePath, AppContext.BaseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        builder.Services.AddKeyVaultMcpPersistence(dbPath);
        builder.Services.AddKeyVaultMcpHttpServer();

        var app = builder.Build();
        app.Services.InitializeKeyVaultMcpAsync().GetAwaiter().GetResult();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapKeyVaultMcp(routePrefix);

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GnOuGo.KeyVault.Mcp.Startup");
        logger.LogInformation(
            "GnOuGo.KeyVault.Mcp HTTP server configured — baseDirectory={BaseDirectory}, keyVaultDb={KeyVaultDbPath}, routePrefix={RoutePrefix}.",
            AppContext.BaseDirectory,
            dbPath,
            routePrefix);

        return app;
    }
}

