namespace GnOuGo.Agent.Mcp.Models;

/// <summary>
/// Stores persisted user defaults for the local agent experience.
/// </summary>
public sealed class UserConfigRecord
{
    public Guid Id { get; set; }

    /// <summary>Required tenant identifier (multi-tenant).</summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Normalized scope key used to enforce a single config row per tenant,
    /// including the local single-tenant/null-tenant case.
    /// </summary>
    public string TenantScopeKey { get; set; } = "global";

    public string? DefaultLlmProvider { get; set; }

    public string? DefaultLlmModel { get; set; }

    public string? DefaultEmbeddingConfig { get; set; }

    public string? DefaultAgent { get; set; }

    public long UpdatedAtTicks { get; set; }

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtTicks == 0
            ? DateTimeOffset.UnixEpoch
            : new DateTimeOffset(UpdatedAtTicks, TimeSpan.Zero);
        set => UpdatedAtTicks = value.UtcTicks;
    }
}

