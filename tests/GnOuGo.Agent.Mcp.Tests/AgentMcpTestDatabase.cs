using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Mcp.Tests;

internal sealed class AgentMcpTestDatabase : IDisposable
{
    private readonly string _directory;

    public AgentMcpTestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "gnougo-agent-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        DatabasePath = Path.Combine(_directory, "agent.db");
        Store = new AgentSqliteStore(DatabasePath);
        AgentMcpDatabaseBootstrap.EnsureCreatedAsync(Store).GetAwaiter().GetResult();

        DiffDb = new DiffDbContext(new DbContextOptionsBuilder<DiffDbContext>()
            .UseDiffCoreSqlite($"Data Source={DatabasePath}")
            .Options);
        DiffService = new DiffService(DiffDb);
    }

    public string DatabasePath { get; }

    public AgentSqliteStore Store { get; }

    public DiffDbContext DiffDb { get; }

    public DiffService DiffService { get; }

    public AgentRepository CreateAgentRepository() => new(Store, DiffService);

    public UserConfigRepository CreateUserConfigRepository() => new(Store, DiffService);

    public void Dispose()
    {
        DiffDb.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

