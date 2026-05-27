using Microsoft.Data.Sqlite;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class AgentSqliteStore
{
    public AgentSqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }
}

