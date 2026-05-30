using System.Text.Json;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Diff.Core.Models;
using GnOuGo.Diff.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class AgentRepository : IAgentRepository
{
    private readonly AgentMcpDbContext _db;
    private readonly DiffService _diff;

    private const string DiffEntityType = "AgentDefinition";
    private const string DiffAuthor = "GnOuGo.Agent.Mcp";

    public AgentRepository(AgentMcpDbContext db, DiffService diff)
    {
        _db = db;
        _diff = diff;
    }

    public async Task<AgentDefinition> AddAgentAsync(string name, string workflow, List<Schedule> schedules, string? originalPrompt = null, string? scheduleDescription = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var normalizedName = NormalizeName(name);
        await EnsureNameAvailableAsync(normalizedName, excludedAgentId: null, ct);

        EnsureScheduleIds(schedules);

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            Workflow = workflow,
            OriginalPrompt = originalPrompt,
            ScheduleDescription = scheduleDescription,
            SchedulesJson = JsonSerializer.Serialize(schedules, AgentMcpJsonContext.Default.ListSchedule),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(ct);
        await SaveRevisionAsync(agent, ct);
        return agent;
    }

    public async Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, List<Schedule> schedules, string? originalPrompt = null, string? scheduleDescription = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var normalizedName = NormalizeName(name);
        await EnsureNameAvailableAsync(normalizedName, id, ct);

        var agent = await AgentMcpQueries.GetAgentById(_db, id)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        EnsureScheduleIds(schedules);

        agent.Name = normalizedName;
        agent.Workflow = workflow;
        agent.OriginalPrompt = originalPrompt;
        agent.ScheduleDescription = scheduleDescription;
        agent.SchedulesJson = JsonSerializer.Serialize(schedules, AgentMcpJsonContext.Default.ListSchedule);
        agent.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        await SaveRevisionAsync(agent, ct);
        return agent;
    }

    public async Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
    {
        return await _db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
    }

    public async Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalizedName = NormalizeName(name);
        return await AgentMcpQueries.GetAgentByName(_db, normalizedName);
    }

    public async Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
    {
        var agent = await AgentMcpQueries.GetAgentById(_db, id)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        _db.Agents.Remove(agent);
        await _db.SaveChangesAsync(ct);

        await SaveRevisionAsync(
            id.ToString(),
            $"# Agent deleted\nid: {id}\nname: {agent.Name}\n",
            ct);
    }

    public static List<Schedule> DeserializeSchedules(string schedulesJson)
        => JsonSerializer.Deserialize(schedulesJson, AgentMcpJsonContext.Default.ListSchedule) ?? [];

    internal static string SerializeAgentToYaml(AgentDefinition agent)
    {
        var snapshot = new AgentSnapshot
        {
            Id = agent.Id.ToString(),
            Name = agent.Name,
            Workflow = agent.Workflow,
            OriginalPrompt = agent.OriginalPrompt ?? "",
            ScheduleDescription = agent.ScheduleDescription ?? "",
            Schedules = DeserializeSchedules(agent.SchedulesJson)
                .Select(static schedule => new ScheduleSnapshot
                {
                    Id = schedule.Id,
                    Name = schedule.Name,
                    Cron = schedule.Cron
                })
                .ToArray(),
            CreatedAt = agent.CreatedAt.ToString("o"),
            UpdatedAt = agent.UpdatedAt.ToString("o")
        };

        return AgentMcpYamlContext.Serialize(snapshot);
    }

    private async Task EnsureNameAvailableAsync(string normalizedName, Guid? excludedAgentId, CancellationToken ct)
    {
        AgentDefinition? existing;
        if (excludedAgentId.HasValue)
            existing = await AgentMcpQueries.GetAgentByNameExcluding(_db, normalizedName, excludedAgentId.Value);
        else
            existing = await AgentMcpQueries.GetAgentByName(_db, normalizedName);

        if (existing is not null)
            throw new DuplicateAgentNameException(normalizedName);
    }

    private static void EnsureScheduleIds(List<Schedule> schedules)
    {
        foreach (var schedule in schedules)
        {
            if (string.IsNullOrEmpty(schedule.Id))
                schedule.Id = Guid.NewGuid().ToString("N")[..12];
        }
    }

    private async Task SaveRevisionAsync(AgentDefinition agent, CancellationToken ct)
        => await SaveRevisionAsync(agent.Id.ToString(), SerializeAgentToYaml(agent), ct);

    private async Task SaveRevisionAsync(string entityId, string currentValue, CancellationToken ct)
        => await _diff.CreateRevisionAsync(new CreateRevisionRequest(
            DiffEntityType,
            entityId,
            currentValue,
            DiffAuthor), ct);

    private static string NormalizeName(string name) => name.Trim();
}
