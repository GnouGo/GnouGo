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
    // ── Agent queries ────────────────────────────────────────────────

    public static Task<AgentDefinition?> GetAgentById(AgentMcpDbContext db, Guid id)
        => db.Agents.FirstOrDefaultAsync(a => a.Id == id);

    public static Task<AgentDefinition?> GetAgentByName(AgentMcpDbContext db, string name)
        => db.Agents.FirstOrDefaultAsync(a => a.Name.ToUpper() == name.ToUpper());

    public static Task<AgentDefinition?> GetAgentByNameExcluding(AgentMcpDbContext db, string name, Guid excludedId)
        => db.Agents.FirstOrDefaultAsync(a => a.Name.ToUpper() == name.ToUpper() && a.Id != excludedId);

    // ── UserConfig queries ───────────────────────────────────────────

    public static Task<UserConfigRecord?> GetUserConfigByScope(AgentMcpDbContext db, string tenantScopeKey)
        => db.UserConfigs.FirstOrDefaultAsync(c => c.TenantScopeKey == tenantScopeKey);
}
