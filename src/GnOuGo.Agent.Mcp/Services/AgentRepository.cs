using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using GnOuGo.Agent.Mcp.Data;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Diff.Core.Models;
using GnOuGo.Diff.Core.Services;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class AgentRepository : IAgentRepository
{
    private readonly AgentDbContext _db;
    private readonly DiffService _diff;

    private const string DiffEntityType = "AgentDefinition";
    private const string DiffAuthor = "GnOuGo.Agent.Mcp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public AgentRepository(AgentDbContext db, DiffService diff)
    {
        _db = db;
        _diff = diff;
    }

    public async Task<AgentDefinition> AddAgentAsync(string name, string workflow, List<Schedule> schedules, string? originalPrompt = null, string? scheduleDescription = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        // Ensure every schedule has an id
        foreach (var s in schedules)
        {
            if (string.IsNullOrEmpty(s.Id))
                s.Id = Guid.NewGuid().ToString("N")[..12];
        }

        var now = DateTimeOffset.UtcNow;
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Workflow = workflow,
            OriginalPrompt = originalPrompt,
            ScheduleDescription = scheduleDescription,
            SchedulesJson = JsonSerializer.Serialize(schedules, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(ct);

        await SaveRevisionAsync(agent);

        return agent;
    }

    public async Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, List<Schedule> schedules, string? originalPrompt = null, string? scheduleDescription = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var agent = await _db.Agents.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        // Ensure every schedule has an id
        foreach (var s in schedules)
        {
            if (string.IsNullOrEmpty(s.Id))
                s.Id = Guid.NewGuid().ToString("N")[..12];
        }

        agent.Name = name.Trim();
        agent.Workflow = workflow;
        agent.OriginalPrompt = originalPrompt;
        agent.ScheduleDescription = scheduleDescription;
        agent.SchedulesJson = JsonSerializer.Serialize(schedules, JsonOptions);
        agent.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        await SaveRevisionAsync(agent);

        return agent;
    }

    public async Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
        => await _db.Agents.OrderBy(a => a.Name).ToListAsync(ct);

    public async Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
        => await _db.Agents.FirstOrDefaultAsync(
            a => a.Name.ToLower() == name.Trim().ToLower(), ct);

    public async Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
    {
        var agent = await _db.Agents.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        _db.Agents.Remove(agent);
        await _db.SaveChangesAsync(ct);

        // Save a "deleted" tombstone revision
        await _diff.CreateRevisionAsync(new CreateRevisionRequest(
            DiffEntityType,
            id.ToString(),
            $"# Agent deleted\nid: {id}\nname: {agent.Name}\n",
            DiffAuthor));
    }

    /// <summary>Deserialize the schedules JSON from an <see cref="AgentDefinition"/>.</summary>
    public static List<Schedule> DeserializeSchedules(string schedulesJson)
        => JsonSerializer.Deserialize<List<Schedule>>(schedulesJson, JsonOptions) ?? [];

    // ── Diff helpers ─────────────────────────────────────────────────

    private async Task SaveRevisionAsync(AgentDefinition agent)
    {
        var yaml = SerializeAgentToYaml(agent);
        await _diff.CreateRevisionAsync(new CreateRevisionRequest(
            DiffEntityType,
            agent.Id.ToString(),
            yaml,
            DiffAuthor));
    }

    internal static string SerializeAgentToYaml(AgentDefinition agent)
    {
        var snapshot = new AgentSnapshot
        {
            Id = agent.Id.ToString(),
            Name = agent.Name,
            Workflow = agent.Workflow,
            OriginalPrompt = agent.OriginalPrompt ?? "",
            ScheduleDescription = agent.ScheduleDescription ?? "",
            Schedules = DeserializeSchedules(agent.SchedulesJson),
            CreatedAt = agent.CreatedAt.ToString("o"),
            UpdatedAt = agent.UpdatedAt.ToString("o")
        };

        return YamlSerializer.Serialize(snapshot);
    }

    /// <summary>Flat DTO used only for YAML serialization of an agent snapshot.</summary>
    internal sealed class AgentSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Workflow { get; set; } = "";
        public string OriginalPrompt { get; set; } = "";
        public string ScheduleDescription { get; set; } = "";
        public List<Schedule> Schedules { get; set; } = [];
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }
}
