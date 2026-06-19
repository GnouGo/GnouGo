using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Agent.Mcp.Services;

namespace GnOuGo.Agent.Mcp;

[McpServerToolType]
public sealed class AgentTools
{
    private readonly IAgentRepository _repo;
    private readonly ILogger<AgentTools> _logger;

    public AgentTools(IAgentRepository repo, ILogger<AgentTools> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // ── Add Agent ────────────────────────────────────────────────────

    [McpServerTool(Name = "agent_add"), Description(
        "Create a new agent with a name and workflow definition. " +
        "Returns { success, agent } or { success: false, error_code, error_message }.")]
    public async Task<AgentToolResult> AgentAdd(
        [Description("Agent name (required).")]
        string name,
        [Description("Workflow definition text (required).")]
        string workflow,
        [Description("Original natural-language prompt used to generate the workflow.")]
        string? originalPrompt = null)
    {
        try
        {
            var agent = await _repo.AddAgentAsync(name, workflow, originalPrompt);

            return new AgentToolResult(true, SerializeAgent(agent));
        }
        catch (DuplicateAgentNameException ex)
        {
            _logger.LogWarning(ex, "agent_add duplicate name");
            return ErrorResult("ALREADY_EXISTS", ex.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "agent_add validation error");
            return ErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_add unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool(Name = "agent_add_bundle"), Description(
        "Create a new agent with a main workflow and persist bundled workflow files at safe workspace-relative paths. " +
        "Returns { success, agent } or { success: false, error_code, error_message }.")]
    public async Task<AgentToolResult> AgentAddBundle(
        [Description("Agent name (required).")]
        string name,
        [Description("Main workflow definition text (required).")]
        string workflow,
        [Description("Map of safe relative workflow file paths to YAML content.")]
        Dictionary<string, string>? workflows = null,
        [Description("Original natural-language prompt used to generate the workflow.")]
        string? originalPrompt = null)
    {
        try
        {
            var agent = await _repo.AddAgentBundleAsync(name, workflow, workflows, originalPrompt);

            return new AgentToolResult(true, SerializeAgent(agent));
        }
        catch (DuplicateAgentNameException ex)
        {
            _logger.LogWarning(ex, "agent_add_bundle duplicate name");
            return ErrorResult("ALREADY_EXISTS", ex.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "agent_add_bundle validation error");
            return ErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_add_bundle unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Update Agent ─────────────────────────────────────────────────

    [McpServerTool(Name = "agent_update"), Description(
        "Update an existing agent's name and workflow. " +
        "Returns { success, agent } or { success: false, error_code, error_message }.")]
    public async Task<AgentToolResult> AgentUpdate(
        [Description("Agent identifier (GUID, required).")]
        string id,
        [Description("New agent name (required).")]
        string name,
        [Description("New workflow definition text (required).")]
        string workflow,
        [Description("Original natural-language prompt used to generate the workflow.")]
        string? originalPrompt = null)
    {
        try
        {
            if (!Guid.TryParse(id, out var agentId))
                throw new ArgumentException("'id' must be a valid GUID.");

            var agent = await _repo.UpdateAgentAsync(agentId, name, workflow, originalPrompt);

            return new AgentToolResult(true, SerializeAgent(agent));
        }
        catch (DuplicateAgentNameException ex)
        {
            _logger.LogWarning(ex, "agent_update duplicate name");
            return ErrorResult("ALREADY_EXISTS", ex.Message);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "agent_update validation error");
            return ErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "agent_update not found");
            return ErrorResult("NOT_FOUND", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_update unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── List Agents ──────────────────────────────────────────────────

    [McpServerTool(Name = "agent_list"), Description(
        "List all agents. Returns { success, agents: [...] }.")]
    public async Task<AgentListToolResult> AgentList()
    {
        try
        {
            var agents = await _repo.ListAgentsAsync();
            return new AgentListToolResult(true, agents.Select(SerializeAgent).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_list unexpected error");
            return ListErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Delete Agent ─────────────────────────────────────────────────

    [McpServerTool(Name = "agent_delete"), Description(
        "Delete an agent by its identifier. Returns { success } or { success: false, error_code, error_message }.")]
    public async Task<AgentDeleteToolResult> AgentDelete(
        [Description("Agent identifier (GUID, required).")]
        string id)
    {
        try
        {
            if (!Guid.TryParse(id, out var agentId))
                throw new ArgumentException("'id' must be a valid GUID.");

            await _repo.DeleteAgentAsync(agentId);

            return new AgentDeleteToolResult(true, id);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "agent_delete validation error");
            return DeleteErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "agent_delete not found");
            return DeleteErrorResult("NOT_FOUND", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_delete unexpected error");
            return DeleteErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Get Agent By Name ────────────────────────────────────────────

    [McpServerTool(Name = "agent_get_by_name"), Description(
        "Get an agent by its name (case-insensitive). " +
        "Returns { success, agent } or { success: false, error_code, error_message }.")]
    public async Task<AgentToolResult> AgentGetByName(
        [Description("Agent name (required).")]
        string name)
    {
        try
        {
            var agent = await _repo.GetByNameAsync(name);
            if (agent is null)
                return ErrorResult("NOT_FOUND", $"Agent '{name}' not found.");

            return new AgentToolResult(true, SerializeAgent(agent));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_get_by_name unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────


    private static AgentDto SerializeAgent(AgentDefinition agent)
    {
        return new AgentDto(
            agent.Id.ToString(),
            agent.Name,
            agent.Workflow,
            agent.OriginalPrompt,
            agent.CreatedAt.ToString("o"),
            agent.UpdatedAt.ToString("o"));
    }

    private static AgentToolResult ErrorResult(string errorCode, string errorMessage)
        => new(false, ErrorCode: errorCode, ErrorMessage: errorMessage);

    private static AgentListToolResult ListErrorResult(string errorCode, string errorMessage)
        => new(false, ErrorCode: errorCode, ErrorMessage: errorMessage);

    private static AgentDeleteToolResult DeleteErrorResult(string errorCode, string errorMessage)
        => new(false, ErrorCode: errorCode, ErrorMessage: errorMessage);
}
