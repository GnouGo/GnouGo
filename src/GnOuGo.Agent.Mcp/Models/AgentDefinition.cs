namespace GnOuGo.Agent.Mcp.Models;

/// <summary>
/// Represents an agent definition with a workflow and metadata.
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

