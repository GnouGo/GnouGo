namespace GnOuGo.Agent.Mcp.Models;

/// <summary>
/// A schedule entry associated with an agent (stored as JSON inside <see cref="AgentDefinition.SchedulesJson"/>).
/// </summary>
public sealed class Schedule
{
    /// <summary>Auto-generated unique identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Human-readable schedule name.</summary>
    public required string Name { get; set; }

    /// <summary>Kubernetes-style cron expression (5 fields: minute hour day-of-month month day-of-week).</summary>
    public required string Cron { get; set; }
}

