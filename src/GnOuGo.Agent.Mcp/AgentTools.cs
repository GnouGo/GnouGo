using System.ComponentModel;
using System.Text.Json.Nodes;
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
        "Create a new agent with a name, workflow definition, and optional schedules. " +
        "Returns { success, agent } or { success: false, error_code, error_message }.")]
    public async Task<JsonObject> AgentAdd(
        [Description("Agent name (required).")]
        string name,
        [Description("Workflow definition text (required).")]
        string workflow,
        [Description("Array of schedules, each with 'name' (string) and 'cron' (string, Kubernetes cron format). " +
                     "Example: [{\"name\":\"daily\",\"cron\":\"0 8 * * *\"}]. Omit for no schedules.")]
        Schedule[]? schedules = null)
    {
        try
        {
            var list = schedules?.ToList() ?? [];
            var agent = await _repo.AddAgentAsync(name, workflow, list);

            return new JsonObject
            {
                ["success"] = true,
                ["agent"] = SerializeAgent(agent)
            };
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

    // ── Update Agent ─────────────────────────────────────────────────

    [McpServerTool(Name = "agent_update"), Description(
        "Update an existing agent's name, workflow, and schedules. " +
        "Returns { success, agent } or { success: false, error_code, error_message }.")]
    public async Task<JsonObject> AgentUpdate(
        [Description("Agent identifier (GUID, required).")]
        string id,
        [Description("New agent name (required).")]
        string name,
        [Description("New workflow definition text (required).")]
        string workflow,
        [Description("Array of schedules, each with 'name' and 'cron'. Pass an empty array to clear schedules.")]
        Schedule[]? schedules = null)
    {
        try
        {
            if (!Guid.TryParse(id, out var agentId))
                throw new ArgumentException("'id' must be a valid GUID.");

            var list = schedules?.ToList() ?? [];
            var agent = await _repo.UpdateAgentAsync(agentId, name, workflow, list);

            return new JsonObject
            {
                ["success"] = true,
                ["agent"] = SerializeAgent(agent)
            };
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
    public async Task<JsonObject> AgentList()
    {
        try
        {
            var agents = await _repo.ListAgentsAsync();

            var arr = new JsonArray();
            foreach (var a in agents)
                arr.Add(SerializeAgent(a));

            return new JsonObject
            {
                ["success"] = true,
                ["agents"] = arr
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_list unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Delete Agent ─────────────────────────────────────────────────

    [McpServerTool(Name = "agent_delete"), Description(
        "Delete an agent by its identifier. Returns { success } or { success: false, error_code, error_message }.")]
    public async Task<JsonObject> AgentDelete(
        [Description("Agent identifier (GUID, required).")]
        string id)
    {
        try
        {
            if (!Guid.TryParse(id, out var agentId))
                throw new ArgumentException("'id' must be a valid GUID.");

            await _repo.DeleteAgentAsync(agentId);

            return new JsonObject
            {
                ["success"] = true,
                ["deleted_id"] = id
            };
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "agent_delete validation error");
            return ErrorResult("INVALID_INPUT", ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "agent_delete not found");
            return ErrorResult("NOT_FOUND", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "agent_delete unexpected error");
            return ErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────


    private static JsonObject SerializeAgent(AgentDefinition agent)
    {
        var schedulesNode = JsonNode.Parse(agent.SchedulesJson) ?? new JsonArray();

        return new JsonObject
        {
            ["id"] = agent.Id.ToString(),
            ["name"] = agent.Name,
            ["workflow"] = agent.Workflow,
            ["schedules"] = schedulesNode,
            ["created_at"] = agent.CreatedAt.ToString("o"),
            ["updated_at"] = agent.UpdatedAt.ToString("o")
        };
    }

    private static JsonObject ErrorResult(string errorCode, string errorMessage)
        => new()
        {
            ["success"] = false,
            ["error_code"] = errorCode,
            ["error_message"] = errorMessage
        };
}

