using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.KeyVault.Mcp;

var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// ── Logging to stderr (stdout is reserved for MCP stdio) ────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// ── Database ─────────────────────────────────────────────────────────
var dbRelativePath = builder.Configuration.GetValue<string>("KeyVault:DatabasePath")
    ?? "data/gnougo-keyvault.db";
var dbPath = Path.IsPathRooted(dbRelativePath)
    ? dbRelativePath
    : Path.Combine(AppContext.BaseDirectory, dbRelativePath);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<KeyVaultDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<KeyVaultService>();
builder.Services.AddTransient<KeyVaultTools>();

// ── MCP server (stdio transport) ─────────────────────────────────────
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.KeyVault.Mcp",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<KeyVaultTools>();

var host = builder.Build();

// ── Bootstrap DB & default key pair ──────────────────────────────────
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
    await db.Database.EnsureCreatedAsync();
    var svc = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
    await svc.EnsureDefaultKeyPairAsync();
}

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("GnOuGo.KeyVault.Mcp.Startup");
startupLogger.LogInformation(
    "KeyVault MCP server starting — db={DbPath}, contentRoot={ContentRoot}",
    dbPath, builder.Environment.ContentRootPath);

await host.RunAsync();

