namespace GnOuGo.Agent.Server.Configuration;

/// <summary>
/// Typed configuration for short-lived model catalog caching.
/// </summary>
public sealed class ModelCatalogCacheSettings
{
    public const string SectionName = "ModelCatalogCache";

    /// <summary>
    /// Enables in-memory caching for provider model discovery results.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Absolute cache lifetime in seconds.
    /// </summary>
    public int AbsoluteExpirationSeconds { get; set; } = 300;
}

