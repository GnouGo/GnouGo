namespace GnOuGo.Agent.Mcp.Models;

/// <summary>
/// Represents an agent definition with a workflow and associated schedules.
/// </summary>
public sealed class AgentDefinition
{
    public Guid Id { get; set; }

    /// <summary>Required tenant identifier (multi-tenant).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Human-readable agent name.</summary>
    public required string Name { get; set; }

    /// <summary>Workflow definition (free-form text / YAML / JSON).</summary>
    public required string Workflow { get; set; }

    /// <summary>Original natural-language prompt used to generate the workflow.</summary>
    public string? OriginalPrompt { get; set; }

    /// <summary>Human-readable schedule description before cron conversion.</summary>
    public string? ScheduleDescription { get; set; }

    /// <summary>Schedules serialized as a JSON array of <see cref="Schedule"/>.</summary>
    public string SchedulesJson { get; set; } = "[]";

    public long CreatedAtTicks { get; set; }
    public long UpdatedAtTicks { get; set; }

    public DateTimeOffset CreatedAt
    {
        get => new(CreatedAtTicks, TimeSpan.Zero);
        set => CreatedAtTicks = value.UtcTicks;
    }

    public DateTimeOffset UpdatedAt
    {
        get => new(UpdatedAtTicks, TimeSpan.Zero);
        set => UpdatedAtTicks = value.UtcTicks;
    }
}

