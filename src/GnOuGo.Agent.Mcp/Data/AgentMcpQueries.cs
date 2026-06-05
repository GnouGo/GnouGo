using GnOuGo.Agent.Mcp.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Mcp.Data;

/// <summary>
/// Encapsulated EF Core queries for Agent MCP.
/// Uses standard LINQ queries which benefit from EF Core's internal query plan caching
/// without the static model binding issues of EF.CompileAsyncQuery across multiple DbContext registrations.
/// </summary>
internal static class AgentMcpQueries
{

    // ── UserConfig queries ───────────────────────────────────────────

    public static Task<UserConfigRecord?> GetUserConfigByScope(AgentMcpDbContext db, string tenantScopeKey)
        => db.UserConfigs.FirstOrDefaultAsync(c => c.TenantScopeKey == tenantScopeKey);
}
