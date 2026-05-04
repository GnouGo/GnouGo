namespace GnOuGo.Observability.Core;

/// <summary>
/// Shared OpenTelemetry configuration bound from the <c>OpenTelemetry</c> section.
/// </summary>
public sealed class OpenTelemetrySettings
{
    public const string SectionName = "OpenTelemetry";

    /// <summary>Enables OpenTelemetry traces, metrics, and logs.</summary>
    public bool Enabled { get; set; }

    /// <summary>Logical service name exported in OpenTelemetry resources.</summary>
    public string ServiceName { get; set; } = "GnOuGo";

    /// <summary>Service version exported in OpenTelemetry resources.</summary>
    public string ServiceVersion { get; set; } = "1.0.0";

    /// <summary>OTLP endpoint, for example <c>http://127.0.0.1:4317</c>.</summary>
    public string OtlpEndpoint { get; set; } = "http://127.0.0.1:4317";

    /// <summary>OTLP protocol: <c>Grpc</c> or <c>HttpProtobuf</c>.</summary>
    public string Protocol { get; set; } = "Grpc";

    /// <summary>Optional tenant identifier propagated as the <c>X-Tenant-Id</c> OTLP header.</summary>
    public string? TenantId { get; set; }

    /// <summary>Enables OpenTelemetry log export.</summary>
    public bool IncludeLogs { get; set; } = true;

    /// <summary>Enables OpenTelemetry metric export.</summary>
    public bool IncludeMetrics { get; set; } = true;

    /// <summary>Enables outgoing HTTP client traces and metrics.</summary>
    public bool IncludeHttpClientInstrumentation { get; set; } = true;

    /// <summary>Enables ASP.NET Core request traces and metrics for HTTP MCP hosts.</summary>
    public bool IncludeAspNetCoreTraces { get; set; }

    /// <summary>Additional ActivitySource names to subscribe to.</summary>
    public string[] ActivitySources { get; set; } = [];

    /// <summary>Additional Meter names to subscribe to.</summary>
    public string[] Meters { get; set; } = [];
}

