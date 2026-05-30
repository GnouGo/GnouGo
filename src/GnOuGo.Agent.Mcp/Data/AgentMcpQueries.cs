using GnOuGo.Agent.Mcp.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Mcp.Data;

/// <summary>
/// Precompiled EF Core queries for optimal performance and AOT friendliness.
/// </summary>
internal static class AgentMcpQueries
{
    // ── Agent queries ────────────────────────────────────────────────

    public static readonly Func<AgentMcpDbContext, Guid, Task<AgentDefinition?>> GetAgentById =
        EF.CompileAsyncQuery(
            (AgentMcpDbContext db, Guid id) =>
                db.Agents.FirstOrDefault(a => a.Id == id));

    public static readonly Func<AgentMcpDbContext, string, Task<AgentDefinition?>> GetAgentByName =
        EF.CompileAsyncQuery(
            (AgentMcpDbContext db, string name) =>
                db.Agents.FirstOrDefault(a => a.Name.ToUpper() == name.ToUpper()));

    public static readonly Func<AgentMcpDbContext, string, Guid, Task<AgentDefinition?>> GetAgentByNameExcluding =
        EF.CompileAsyncQuery(
            (AgentMcpDbContext db, string name, Guid excludedId) =>
                db.Agents.FirstOrDefault(a => a.Name.ToUpper() == name.ToUpper() && a.Id != excludedId));

    // ── UserConfig queries ───────────────────────────────────────────

    public static readonly Func<AgentMcpDbContext, string, Task<UserConfigRecord?>> GetUserConfigByScope =
        EF.CompileAsyncQuery(
            (AgentMcpDbContext db, string tenantScopeKey) =>
                db.UserConfigs.FirstOrDefault(c => c.TenantScopeKey == tenantScopeKey));
}

