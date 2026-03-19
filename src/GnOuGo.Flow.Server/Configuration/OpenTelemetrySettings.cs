namespace GnOuGo.Flow.Server.Configuration;

/// <summary>
/// Typed configuration for OpenTelemetry, bound from appsettings.json section "OpenTelemetry".
/// </summary>
public sealed class OpenTelemetrySettings
{
    public const string SectionName = "OpenTelemetry";

    /// <summary>Enable or disable OpenTelemetry export.</summary>
    public bool Enabled { get; set; }

    /// <summary>OTLP service name (default: "GnOuGo.Flow.Server").</summary>
    public string ServiceName { get; set; } = "GnOuGo.Flow.Server";

    /// <summary>OTLP endpoint (e.g., "http://localhost:4317").</summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>OTLP protocol: "Grpc" or "HttpProtobuf".</summary>
    public string Protocol { get; set; } = "Grpc";

    /// <summary>Optional tenant identifier sent as header.</summary>
    public string? TenantId { get; set; }
}

