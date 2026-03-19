using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Cli;

/// <summary>
/// Configuration OpenTelemetry pour le CLI DocIngestor.
/// Exporte les traces et métriques vers un collecteur OTLP.
/// </summary>
public static class OpenTelemetryConfiguration
{
    /// <summary>
    /// Configure OpenTelemetry avec exportation vers un collecteur OTLP.
    /// </summary>
    /// <param name="serviceName">Nom du service</param>
    /// <param name="otlpEndpoint">URL du collecteur OTLP</param>
    /// <param name="tenantId">Identifiant du tenant</param>
    /// <param name="protocol">Protocole OTLP (défaut: Grpc)</param>
    public static (TracerProvider?, MeterProvider?) ConfigureOpenTelemetry(
        string serviceName,
        string otlpEndpoint,
        string? tenantId = null,
        OtlpExportProtocol protocol = OtlpExportProtocol.Grpc)
    {
        var attributes = new Dictionary<string, object>
        {
            ["deployment.environment"] = "production",
            ["service.namespace"] = "GnOuGo-agent",
            ["host.name"] = Environment.MachineName
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            attributes["tenant.id"] = tenantId;
        }

        var resourceBuilder = ResourceBuilder
            .CreateDefault()
            .AddService(serviceName, serviceVersion: "1.0.0")
            .AddAttributes(attributes);

        // Configuration des Traces
        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .AddSource(GenAiTelemetry.GetActivitySource().Name)
            .AddHttpClientInstrumentation(options =>
            {
                // Filtrer pour ne garder que les requêtes importantes
                options.FilterHttpRequestMessage = (httpRequestMessage) =>
                {
                    // Ne pas tracer les requêtes vers le collecteur OTLP lui-même
                    if (httpRequestMessage.RequestUri?.ToString().Contains(otlpEndpoint) == true)
                        return false;
                    
                    return true;
                };
                
                // Enrichir les spans HTTP avec plus d'informations
                options.EnrichWithHttpRequestMessage = (activity, request) =>
                {
                    // Définir un nom de span plus descriptif
                    var method = request.Method.ToString();
                    var host = request.RequestUri?.Host ?? "unknown";
                    var path = request.RequestUri?.AbsolutePath ?? "/";
                    
                    activity.DisplayName = $"{method} {host}{path}";
                    activity.SetTag("http.request.method", method);
                    activity.SetTag("http.url", request.RequestUri?.ToString());
                    activity.SetTag("http.host", host);
                    activity.SetTag("http.target", path);
                };
                
                options.EnrichWithHttpResponseMessage = (activity, response) =>
                {
                    activity.SetTag("http.response.status_code", (int)response.StatusCode);
                    activity.SetTag("http.response.status_text", response.ReasonPhrase);
                };
                
                // Enregistrer les exceptions
                options.RecordException = true;
            })
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                options.Protocol = protocol;
                // CRITIQUE: SimpleExportProcessor exporte chaque span APRÈS Activity.Stop()
                // Batch (défaut) peut exporter AVANT la fermeture → EndTimeUnixNano = 0
                options.ExportProcessorType = ExportProcessorType.Simple;
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    options.Headers = $"X-Tenant-Id={tenantId}";
                }
            })
            .Build();

        // Configuration des Métriques
        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .AddMeter(GenAiTelemetry.GetMeter().Name)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter((options, readerOptions) =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
                options.Protocol = protocol;
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    options.Headers = $"X-Tenant-Id={tenantId}";
                }

                // Export toutes les 10 secondes
                readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10000;
            })
            .Build();

        Console.WriteLine($"[OpenTelemetry] Configured with endpoint: {otlpEndpoint}");
        Console.WriteLine($"[OpenTelemetry] Protocol: {protocol}");
        Console.WriteLine($"[OpenTelemetry] Service: {serviceName}");
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            Console.WriteLine($"[OpenTelemetry] Tenant ID: {tenantId}");
        }

        return (tracerProvider, meterProvider);
    }

    /// <summary>
    /// Nettoie les providers OpenTelemetry.
    /// </summary>
    public static void Shutdown(TracerProvider? tracerProvider, MeterProvider? meterProvider)
    {
        try
        {
            tracerProvider?.ForceFlush();
            meterProvider?.ForceFlush();
            
            tracerProvider?.Dispose();
            meterProvider?.Dispose();
            
            Console.WriteLine("[OpenTelemetry] Providers shut down successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenTelemetry] Error during shutdown: {ex.Message}");
        }
    }
}

