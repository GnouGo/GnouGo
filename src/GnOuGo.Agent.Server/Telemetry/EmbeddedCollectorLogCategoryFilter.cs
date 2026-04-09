namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>
/// Defense-in-depth filter that prevents circular telemetry ingestion.
/// Log categories emitted by the embedded OTLP collector pipeline (batch writer,
/// EF Core database commands, gRPC transport, ASP.NET routing, HttpClient OTLP
/// exports) are excluded from the <see cref="CollectorLoggerProvider"/> and from
/// the OpenTelemetry SDK log exporter so they cannot be re-ingested and create
/// an infinite INSERT-into-log_records feedback loop.
/// </summary>
internal static class EmbeddedCollectorLogCategoryFilter
{
    private static readonly string[] SuppressedCategoryPrefixes =
    [
        "OtlpTenantCollector",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.Routing.EndpointMiddleware",
        "Grpc.AspNetCore.Server",
        "System.Net.Http.HttpClient"
    ];

    public static bool ShouldCapture(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return true;

        foreach (var prefix in SuppressedCategoryPrefixes)
        {
            if (categoryName.StartsWith(prefix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

