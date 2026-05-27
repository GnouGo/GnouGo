using System.Text.Json;
using GnOuGo.Agent.Mcp.Models;
using GnOuGo.Diff.Core.Models;
using GnOuGo.Diff.Core.Services;
using Microsoft.Data.Sqlite;

namespace GnOuGo.Agent.Mcp.Services;

public sealed class AgentRepository : IAgentRepository
{
    private readonly AgentSqliteStore _store;
    private readonly DiffService _diff;

    private const string DiffEntityType = "AgentDefinition";
    private const string DiffAuthor = "GnOuGo.Agent.Mcp";

    public AgentRepository(AgentSqliteStore store, DiffService diff)
    {
        _store = store;
        _diff = diff;
    }

    public async Task<AgentDefinition> AddAgentAsync(string name, string workflow, List<Schedule> schedules, string? originalPrompt = null, string? scheduleDescription = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var normalizedName = NormalizeName(name);
        await using var connection = _store.OpenConnection();
        await EnsureNameAvailableAsync(connection, normalizedName, excludedAgentId: null, ct);

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

        await InsertAgentAsync(connection, agent, ct);
        await SaveRevisionAsync(agent, ct);
        return agent;
    }

    public async Task<AgentDefinition> UpdateAgentAsync(Guid id, string name, string workflow, List<Schedule> schedules, string? originalPrompt = null, string? scheduleDescription = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);

        var normalizedName = NormalizeName(name);
        await using var connection = _store.OpenConnection();
        await EnsureNameAvailableAsync(connection, normalizedName, id, ct);

        var agent = await GetByIdAsync(connection, id, ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        EnsureScheduleIds(schedules);

        agent.Name = normalizedName;
        agent.Workflow = workflow;
        agent.OriginalPrompt = originalPrompt;
        agent.ScheduleDescription = scheduleDescription;
        agent.SchedulesJson = JsonSerializer.Serialize(schedules, AgentMcpJsonContext.Default.ListSchedule);
        agent.UpdatedAt = DateTimeOffset.UtcNow;

        await UpdateAgentRowAsync(connection, agent, ct);
        await SaveRevisionAsync(agent, ct);
        return agent;
    }

    public async Task<List<AgentDefinition>> ListAgentsAsync(CancellationToken ct = default)
    {
        await using var connection = _store.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "TenantId", "Name", "Workflow", "OriginalPrompt", "ScheduleDescription",
                   "SchedulesJson", "CreatedAtTicks", "UpdatedAtTicks"
            FROM "Agents"
            ORDER BY "Name";
            """;

        var agents = new List<AgentDefinition>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            agents.Add(ReadAgent(reader));
        return agents;
    }

    public async Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        var normalizedName = NormalizeName(name);
        await using var connection = _store.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "TenantId", "Name", "Workflow", "OriginalPrompt", "ScheduleDescription",
                   "SchedulesJson", "CreatedAtTicks", "UpdatedAtTicks"
            FROM "Agents"
            WHERE upper("Name") = upper($name)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", normalizedName);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAgent(reader) : null;
    }

    public async Task DeleteAgentAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = _store.OpenConnection();
        var agent = await GetByIdAsync(connection, id, ct)
            ?? throw new KeyNotFoundException($"Agent '{id}' not found.");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM \"Agents\" WHERE \"Id\" = $id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

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

    private static async Task InsertAgentAsync(SqliteConnection connection, AgentDefinition agent, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "Agents" ("Id", "TenantId", "Name", "Workflow", "OriginalPrompt", "ScheduleDescription", "SchedulesJson", "CreatedAtTicks", "UpdatedAtTicks")
            VALUES ($id, $tenantId, $name, $workflow, $originalPrompt, $scheduleDescription, $schedulesJson, $createdAtTicks, $updatedAtTicks);
            """;
        AddAgentParameters(command, agent);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateAgentRowAsync(SqliteConnection connection, AgentDefinition agent, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "Agents"
            SET "TenantId" = $tenantId,
                "Name" = $name,
                "Workflow" = $workflow,
                "OriginalPrompt" = $originalPrompt,
                "ScheduleDescription" = $scheduleDescription,
                "SchedulesJson" = $schedulesJson,
                "UpdatedAtTicks" = $updatedAtTicks
            WHERE "Id" = $id;
            """;
        AddAgentParameters(command, agent);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddAgentParameters(SqliteCommand command, AgentDefinition agent)
    {
        command.Parameters.AddWithValue("$id", agent.Id.ToString("D"));
        command.Parameters.AddWithValue("$tenantId", ToDbValue(agent.TenantId?.ToString("D")));
        command.Parameters.AddWithValue("$name", agent.Name);
        command.Parameters.AddWithValue("$workflow", agent.Workflow);
        command.Parameters.AddWithValue("$originalPrompt", ToDbValue(agent.OriginalPrompt));
        command.Parameters.AddWithValue("$scheduleDescription", ToDbValue(agent.ScheduleDescription));
        command.Parameters.AddWithValue("$schedulesJson", agent.SchedulesJson);
        command.Parameters.AddWithValue("$createdAtTicks", agent.CreatedAtTicks);
        command.Parameters.AddWithValue("$updatedAtTicks", agent.UpdatedAtTicks);
    }

    private static async Task<AgentDefinition?> GetByIdAsync(SqliteConnection connection, Guid id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "TenantId", "Name", "Workflow", "OriginalPrompt", "ScheduleDescription",
                   "SchedulesJson", "CreatedAtTicks", "UpdatedAtTicks"
            FROM "Agents"
            WHERE "Id" = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAgent(reader) : null;
    }

    private static AgentDefinition ReadAgent(SqliteDataReader reader)
        => new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            Name = reader.GetString(2),
            Workflow = reader.GetString(3),
            OriginalPrompt = reader.IsDBNull(4) ? null : reader.GetString(4),
            ScheduleDescription = reader.IsDBNull(5) ? null : reader.GetString(5),
            SchedulesJson = reader.GetString(6),
            CreatedAtTicks = reader.GetInt64(7),
            UpdatedAtTicks = reader.GetInt64(8)
        };

    private static async Task EnsureNameAvailableAsync(SqliteConnection connection, string normalizedName, Guid? excludedAgentId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id"
            FROM "Agents"
            WHERE upper("Name") = upper($name)
              AND ($excludedId IS NULL OR "Id" <> $excludedId)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$excludedId", ToDbValue(excludedAgentId?.ToString("D")));

        var result = await command.ExecuteScalarAsync(ct);
        if (result is not null && result != DBNull.Value)
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

    private static object ToDbValue(string? value) => value is null ? DBNull.Value : value;
}
