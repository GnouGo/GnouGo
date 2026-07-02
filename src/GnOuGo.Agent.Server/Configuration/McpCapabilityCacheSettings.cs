namespace GnOuGo.Agent.Server.Configuration;

/// <summary>
/// Typed configuration for MCP server capability discovery caching.
/// </summary>
public sealed class McpCapabilityCacheSettings
{
    public const string SectionName = "McpCapabilityCache";

    /// <summary>
    /// Sliding cache lifetime in seconds for MCP tools, prompts, and resources.
    /// </summary>
    public int SlidingExpirationSeconds { get; set; } = 3600;

    public TimeSpan SlidingExpiration
        => TimeSpan.FromSeconds(Math.Max(1, SlidingExpirationSeconds));
}
