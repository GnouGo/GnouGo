using GnOuGo.Agent.Mcp.Models;

namespace GnOuGo.Agent.Mcp.Services;

/// <summary>
/// Public contract for agent CRUD operations.
/// </summary>
public interface IAgentRepository
{
    /// <summary>Create a new agent with the given name, workflow, and optional metadata.</summary>
    Task<AgentDefinition> AddAgentAsync(string name, string workflow, string? originalPrompt = null, CancellationToken ct = default);

    /// <summary>Update an existing agent (name, workflow, metadata).</summary>
    Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, string? originalPrompt = null, CancellationToken ct = default);

    /// <summary>List all agents.</summary>
    Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default);

    /// <summary>Get a single agent by name (case-insensitive).</summary>
    Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Delete an agent by its identifier.</summary>
    Task DeleteAgentAsync(Guid id, CancellationToken ct = default);
}

