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

var builder = DataHostBootstrap.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// ── Database ─────────────────────────────────────────────────────────
var dbRelativePath = builder.Configuration.GetValue<string>("Agent:DatabasePath")
    ?? "data/gnougo-agent.db";
var dbPath = Path.IsPathRooted(dbRelativePath)
    ? dbRelativePath
    : Path.Combine(AppContext.BaseDirectory, dbRelativePath);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<AgentDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDbContext<DiffDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<DiffService>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();

// ── Existing in-memory services ──────────────────────────────────────
builder.Services.AddSingleton<InMemoryChatHistoryStore>();
builder.Services.AddTransient<DataTools>();
builder.Services.AddTransient<AgentTools>();

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
    .WithTools<AgentTools>();

var host = builder.Build();

// ── Bootstrap DB ─────────────────────────────────────────────────────
using (var scope = host.Services.CreateScope())
{
    var agentDb = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
    await agentDb.Database.EnsureCreatedAsync();

    var diffDb = scope.ServiceProvider.GetRequiredService<DiffDbContext>();
    await diffDb.Database.EnsureCreatedAsync();
}

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GnOuGo.Agent.Mcp.Startup");

startupLogger.LogInformation(
    "GnOuGo.Agent.Mcp MCP server started — db={DbPath}, chat history store ready.",
    dbPath);

await host.RunAsync();
