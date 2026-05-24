using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
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

    public AgentRepository(AgentDbContext db, DiffService diff)
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
            Name = normalizedName,
            Workflow = workflow,
            OriginalPrompt = originalPrompt,
            ScheduleDescription = scheduleDescription,
            SchedulesJson = JsonSerializer.Serialize(schedules, AgentRepositoryJsonContext.Default.ListSchedule),
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

        var normalizedName = NormalizeName(name);
        await EnsureNameAvailableAsync(normalizedName, id, ct);

        var agent = await _db.Agents.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        // Ensure every schedule has an id
        foreach (var s in schedules)
        {
            if (string.IsNullOrEmpty(s.Id))
                s.Id = Guid.NewGuid().ToString("N")[..12];
        }

        agent.Name = normalizedName;
        agent.Workflow = workflow;
        agent.OriginalPrompt = originalPrompt;
        agent.ScheduleDescription = scheduleDescription;
        agent.SchedulesJson = JsonSerializer.Serialize(schedules, AgentRepositoryJsonContext.Default.ListSchedule);
        agent.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        await SaveRevisionAsync(agent);

        return agent;
    }

    public async Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
        => await _db.Agents.OrderBy(a => a.Name).ToListAsync(ct);

    public async Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalizedName = NormalizeName(name);
        return await _db.Agents.FirstOrDefaultAsync(
            a => a.Name.ToUpper() == normalizedName.ToUpper(), ct);
    }

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
        => JsonSerializer.Deserialize(schedulesJson, AgentRepositoryJsonContext.Default.ListSchedule) ?? [];

    private async Task EnsureNameAvailableAsync(string normalizedName, Guid? excludedAgentId, CancellationToken ct)
    {
        var existingAgent = await _db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Name.ToUpper() == normalizedName.ToUpper()
                    && (!excludedAgentId.HasValue || a.Id != excludedAgentId.Value),
                ct);

        if (existingAgent is not null)
            throw new DuplicateAgentNameException(normalizedName);
    }

    private static string NormalizeName(string name) => name.Trim();

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

        return AgentSnapshotYamlSerializer.Serialize(snapshot);
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

    private static class AgentSnapshotYamlSerializer
    {
        public static string Serialize(AgentSnapshot snapshot)
        {
            var builder = new StringBuilder();
            AppendScalar(builder, "id", snapshot.Id);
            AppendScalar(builder, "name", snapshot.Name);
            AppendScalar(builder, "workflow", snapshot.Workflow);
            AppendScalar(builder, "originalPrompt", snapshot.OriginalPrompt);
            AppendScalar(builder, "scheduleDescription", snapshot.ScheduleDescription);
            builder.AppendLine("schedules:");
            if (snapshot.Schedules.Count == 0)
            {
                builder.AppendLine("  []");
            }
            else
            {
                foreach (var schedule in snapshot.Schedules)
                {
                    AppendListItemScalar(builder, "id", schedule.Id);
                    AppendIndentedScalar(builder, "name", schedule.Name, 2);
                    AppendIndentedScalar(builder, "cron", schedule.Cron, 2);
                }
            }

            AppendScalar(builder, "createdAt", snapshot.CreatedAt);
            AppendScalar(builder, "updatedAt", snapshot.UpdatedAt);
            return builder.ToString();
        }

        private static void AppendListItemScalar(StringBuilder builder, string name, string? value)
        {
            builder.Append("  - ");
            AppendScalarContent(builder, name, value, 4);
        }

        private static void AppendScalar(StringBuilder builder, string name, string? value)
            => AppendIndentedScalar(builder, name, value, 0);

        private static void AppendIndentedScalar(StringBuilder builder, string name, string? value, int indent)
        {
            builder.Append(' ', indent);
            AppendScalarContent(builder, name, value, indent + 2);
        }

        private static void AppendScalarContent(StringBuilder builder, string name, string? value, int blockIndent)
        {
            var normalized = value ?? string.Empty;
            if (normalized.Contains('\n', StringComparison.Ordinal) || normalized.Contains('\r', StringComparison.Ordinal))
            {
                builder.Append(name).AppendLine(": |-");
                using var reader = new StringReader(normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    builder.Append(' ', blockIndent).AppendLine(line);
                }

                return;
            }

            builder.Append(name).Append(": ").AppendLine(QuoteIfNeeded(normalized));
        }

        private static string QuoteIfNeeded(string value)
        {
            if (value.Length == 0)
                return "\"\"";

            var needsQuoting = value.StartsWith(" ", StringComparison.Ordinal)
                || value.EndsWith(" ", StringComparison.Ordinal)
                || value.Contains(':', StringComparison.Ordinal)
                || value.Contains('#', StringComparison.Ordinal)
                || value.Contains('"', StringComparison.Ordinal)
                || value is "true" or "false" or "null";

            return needsQuoting
                ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : value;
        }
    }
}
