namespace GnOuGo.Agent.Server.Configuration;

/// <summary>
/// Typed configuration for OpenTelemetry, bound from appsettings.json section "OpenTelemetry".
/// </summary>
public sealed class OpenTelemetrySettings
{
    public const string SectionName = "OpenTelemetry";

    /// <summary>Enable or disable OpenTelemetry export.</summary>
    public bool Enabled { get; set; }

    /// <summary>OTLP service name.</summary>
    public string ServiceName { get; set; } = "GnOuGo.Agent.Server";

    /// <summary>OTLP endpoint (e.g., "http://localhost:4317" for gRPC or "http://localhost:4318" for HTTP/protobuf).</summary>
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>OTLP protocol: "Grpc" or "HttpProtobuf".</summary>
    public string Protocol { get; set; } = "Grpc";

    /// <summary>Optional tenant identifier sent as X-Tenant-Id header.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// When true, include ASP.NET Core incoming request traces.
    /// Keep false in the Agent UI host to avoid noisy traces from Blazor circuit endpoints
    /// and focus on one trace per workflow execution.
    /// </summary>
    public bool IncludeAspNetCoreTraces { get; set; }
}

