using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Services;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;

var builder = DataHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// ── Database ─────────────────────────────────────────────────────────
var dbRelativePath = builder.Configuration.GetValue<string>("Agent:DatabasePath")
    ?? "data/gnougo-agent.db";
var keyVaultDbRelativePath = builder.Configuration.GetValue<string>("KeyVault:DatabasePath")
    ?? "data/gnougo-keyvault.db";
var dbPath = Path.IsPathRooted(dbRelativePath)
    ? dbRelativePath
    : Path.Combine(AppContext.BaseDirectory, dbRelativePath);
var keyVaultDbPath = KeyVaultDatabasePathResolver.Resolve(keyVaultDbRelativePath, AppContext.BaseDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(keyVaultDbPath)!);

builder.Services.AddDbContext<AgentDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDbContext<DiffDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDbContext<KeyVaultDbContext>(o =>
    o.UseSqlite($"Data Source={keyVaultDbPath}"));
builder.Services.AddScoped<DiffService>();
builder.Services.AddScoped<KeyVaultService>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();

// ── Existing in-memory services ──────────────────────────────────────
builder.Services.AddSingleton<InMemoryChatHistoryStore>();
builder.Services.AddTransient<DataTools>();
builder.Services.AddTransient<AgentTools>();
builder.Services.AddTransient<KeyVaultTools>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Agent.Mcp",
            Version = "1.0.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<DataTools>()
    .WithTools<AgentTools>()
    .WithTools<KeyVaultTools>();

var host = builder.Build();

// ── Bootstrap DB ─────────────────────────────────────────────────────
using (var scope = host.Services.CreateScope())
{
    var agentDb = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
    await agentDb.Database.EnsureCreatedAsync();

    var diffDb = scope.ServiceProvider.GetRequiredService<DiffDbContext>();
    await diffDb.Database.EnsureCreatedAsync();

    var keyVaultDb = scope.ServiceProvider.GetRequiredService<KeyVaultDbContext>();
    await keyVaultDb.Database.EnsureCreatedAsync();

    var keyVaultService = scope.ServiceProvider.GetRequiredService<KeyVaultService>();
    await keyVaultService.EnsureDefaultKeyPairAsync();
}

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Agent.Mcp.Startup");

startupLogger.LogInformation(
    "GnOuGo.Agent.Mcp stdio MCP server started — contentRoot={ContentRootPath}, currentDirectory={CurrentDirectory}, baseDirectory={BaseDirectory}, agentDb={AgentDbPath}, keyVaultDb={KeyVaultDbPath}.",
    builder.Environment.ContentRootPath,
    Environment.CurrentDirectory,
    AppContext.BaseDirectory,
    dbPath,
    keyVaultDbPath);

await host.RunAsync();
