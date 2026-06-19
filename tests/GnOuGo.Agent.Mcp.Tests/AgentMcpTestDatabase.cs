using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Diff.Core.Data;
using GnOuGo.Diff.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace GnOuGo.Agent.Mcp.Tests;

internal sealed class AgentMcpTestDatabase : IDisposable
{
    private readonly string _directory;

    public AgentMcpTestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "gnougo-agent-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        DatabasePath = Path.Combine(_directory, "agent.db");
        AgentsDirectory = Path.Combine(_directory, "agents");
        var connectionString = $"Data Source={DatabasePath}";

        // Create AgentMcp tables first
        AgentDb = new AgentMcpDbContext(new DbContextOptionsBuilder<AgentMcpDbContext>()
            .UseSqlite(connectionString)
            .Options);
        AgentDb.Database.EnsureCreated();

        // DiffDb shares the same physical database file.
        // EnsureCreated() is a no-op when the DB already exists, so use CreateTables()
        // to add DiffEntries table to the existing database.
        DiffDb = new DiffDbContext(new DbContextOptionsBuilder<DiffDbContext>()
            .UseDiffCoreSqlite(connectionString)
            .Options);
        try
        {
            DiffDb.GetService<IRelationalDatabaseCreator>().CreateTables();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Tables may already exist if test ran previously on the same file
        }

        DiffService = new DiffService(DiffDb);
    }

    public string DatabasePath { get; }

    public string WorkspaceRoot => _directory;

    public string AgentsDirectory { get; }

    public AgentMcpDbContext AgentDb { get; }

    public DiffDbContext DiffDb { get; }

    public DiffService DiffService { get; }

    public AgentRepository CreateAgentRepository() => new(AgentsDirectory);

    public UserConfigRepository CreateUserConfigRepository() => new(AgentDb, DiffService);

    public void Dispose()
    {
        AgentDb.Dispose();
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
