using System.Diagnostics.CodeAnalysis;
using GnOuGo.Agent.Mcp.Data;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Mcp;

internal static class AgentMcpDatabaseBootstrap
{
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "EnsureCreatedAsync is used at startup to bootstrap the SQLite schema. This code path is not executed in the Native AOT bundled MCP tools.")]
    public static async Task EnsureCreatedAsync(AgentMcpDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.EnsureCreatedAsync(ct);
    }
}
