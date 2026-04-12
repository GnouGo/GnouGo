namespace GnOuGo.Agent.Server.Configuration;

/// <summary>
/// Settings used to query the OTLP collector debug API for per-message trace inspection.
/// </summary>
public sealed class TraceDebugSettings
{
    public const string SectionName = "TraceDebug";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base URL of the HTTP OTLP collector UI/API host.
    /// Example: http://localhost:4318
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:4318";

    /// <summary>
    /// Optional service name filter when searching recent traces.
    /// Falls back to OpenTelemetry:ServiceName when empty.
    /// </summary>
    public string? ServiceName { get; set; }

    public int RecentTraceLimit { get; set; } = 20;

    public int RefreshIntervalSeconds { get; set; } = 2;

    public int RequestTimeoutSeconds { get; set; } = 10;
}

