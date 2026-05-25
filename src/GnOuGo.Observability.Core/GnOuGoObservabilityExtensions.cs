using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GnOuGo.Observability.Core;

/// <summary>
/// Shared OpenTelemetry hosting extensions for GnOuGo applications and MCP servers.
/// </summary>
public static class GnOuGoObservabilityExtensions
{
    /// <summary>
    /// Adds config-driven OpenTelemetry to a generic host. Safe for stdio MCP servers because it does not add stdout logging providers.
    /// </summary>
    public static IHostApplicationBuilder AddGnOuGoOpenTelemetry(
        this IHostApplicationBuilder builder,
        string defaultServiceName,
        Action<OpenTelemetrySettings>? configure = null)
    {
        AddGnOuGoOpenTelemetryCore(
            builder.Services,
            builder.Logging,
            builder.Configuration,
            builder.Environment.EnvironmentName,
            defaultServiceName,
            includeAspNetCoreDefault: false,
            configure);
        return builder;
    }

    /// <summary>
    /// Adds config-driven OpenTelemetry to an ASP.NET Core host.
    /// </summary>
    public static WebApplicationBuilder AddGnOuGoOpenTelemetry(
        this WebApplicationBuilder builder,
        string defaultServiceName,
        Action<OpenTelemetrySettings>? configure = null)
    {
        AddGnOuGoOpenTelemetryCore(
            builder.Services,
            builder.Logging,
            builder.Configuration,
            builder.Environment.EnvironmentName,
            defaultServiceName,
            includeAspNetCoreDefault: true,
            configure);
        return builder;
    }

    private static void AddGnOuGoOpenTelemetryCore(
        IServiceCollection services,
        ILoggingBuilder logging,
        IConfiguration configuration,
        string environmentName,
        string defaultServiceName,
        bool includeAspNetCoreDefault,
        Action<OpenTelemetrySettings>? configure)
    {
        services.AddSingleton<IConfigureOptions<OpenTelemetrySettings>, OpenTelemetrySettingsOptionsConfigurator>();

        var settings = OpenTelemetrySettingsOptionsConfigurator.Read(configuration);

        if (string.IsNullOrWhiteSpace(settings.ServiceName))
            settings.ServiceName = defaultServiceName;

        if (includeAspNetCoreDefault && !configuration.GetSection($"{OpenTelemetrySettings.SectionName}:IncludeAspNetCoreTraces").Exists())
            settings.IncludeAspNetCoreTraces = true;

        configure?.Invoke(settings);

        if (!settings.Enabled)
            return;

        var protocol = ResolveProtocol(settings.Protocol);
        var endpoint = ResolveEndpoint(settings.OtlpEndpoint, protocol);
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(settings.ServiceName, serviceVersion: settings.ServiceVersion)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = environmentName,
                ["host.name"] = Environment.MachineName
            });

        var otelBuilder = services.AddOpenTelemetry();

        otelBuilder.WithTracing(tracing =>
        {
            tracing.SetResourceBuilder(resourceBuilder);
            tracing.AddSource(settings.ServiceName);

            foreach (var source in settings.ActivitySources.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
                tracing.AddSource(source);

            if (settings.IncludeAspNetCoreTraces)
                tracing.AddAspNetCoreInstrumentation();

            if (settings.IncludeHttpClientInstrumentation)
                tracing.AddHttpClientInstrumentation();

            tracing.AddOtlpExporter(options => ConfigureExporter(options, settings, endpoint, protocol));
        });

        if (settings.IncludeMetrics)
        {
            otelBuilder.WithMetrics(metrics =>
            {
                metrics.SetResourceBuilder(resourceBuilder);
                metrics.AddMeter(settings.ServiceName);

                foreach (var meter in settings.Meters.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
                    metrics.AddMeter(meter);

                if (settings.IncludeAspNetCoreTraces)
                    metrics.AddAspNetCoreInstrumentation();

                if (settings.IncludeHttpClientInstrumentation)
                    metrics.AddHttpClientInstrumentation();

                metrics.AddOtlpExporter(options => ConfigureExporter(options, settings, endpoint, protocol));
            });
        }

        if (settings.IncludeLogs)
        {
            logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resourceBuilder);
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.ParseStateValues = true;
                options.AddOtlpExporter(exporter => ConfigureExporter(exporter, settings, endpoint, protocol));
            });
        }
    }

    private static OtlpExportProtocol ResolveProtocol(string? protocol)
        => string.Equals(protocol, "HttpProtobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;

    private static Uri ResolveEndpoint(string? endpoint, OtlpExportProtocol protocol)
    {
        if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var configured))
            return configured;

        return protocol == OtlpExportProtocol.HttpProtobuf
            ? new Uri("http://127.0.0.1:4318")
            : new Uri("http://127.0.0.1:4317");
    }

    private static void ConfigureExporter(OtlpExporterOptions options, OpenTelemetrySettings settings, Uri endpoint, OtlpExportProtocol protocol)
    {
        options.Endpoint = endpoint;
        options.Protocol = protocol;

        if (!string.IsNullOrWhiteSpace(settings.TenantId))
            options.Headers = $"X-Tenant-Id={settings.TenantId}";
    }
}

