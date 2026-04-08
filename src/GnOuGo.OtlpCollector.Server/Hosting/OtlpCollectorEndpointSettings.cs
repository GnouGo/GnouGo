using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace OtlpTenantCollector.Hosting;

public sealed class OtlpCollectorEndpointSettings
{
    public const string SectionName = "OtlpCollector";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Host/interface used by the embedded collector listeners.
    /// Use 127.0.0.1 for local-only exposure.
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    public int GrpcPort { get; set; } = 4317;

    public int HttpPort { get; set; } = 4318;

    public bool ExposeHealthEndpoint { get; set; } = true;

    public Uri GetGrpcEndpoint()
        => new($"http://{GetClientHost()}:{GrpcPort}");

    public Uri GetHttpEndpoint()
        => new($"http://{GetClientHost()}:{HttpPort}");

    public string GetClientHost()
        => Host switch
        {
            "0.0.0.0" => "127.0.0.1",
            "*" => "127.0.0.1",
            "+" => "127.0.0.1",
            _ => Host
        };
}

public static class OtlpCollectorEndpointSettingsExtensions
{
    public static OtlpCollectorEndpointSettings ConfigureEmbeddedCollectorEndpoints(
        this ConfigureWebHostBuilder webHost,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(webHost);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = configuration
            .GetSection(OtlpCollectorEndpointSettings.SectionName)
            .Get<OtlpCollectorEndpointSettings>() ?? new OtlpCollectorEndpointSettings();

        if (!settings.Enabled)
            return settings;

        if (settings.GrpcPort <= 0)
            throw new InvalidOperationException($"{OtlpCollectorEndpointSettings.SectionName}:GrpcPort must be greater than 0.");

        if (settings.HttpPort <= 0)
            throw new InvalidOperationException($"{OtlpCollectorEndpointSettings.SectionName}:HttpPort must be greater than 0.");

        if (settings.GrpcPort == settings.HttpPort)
            throw new InvalidOperationException($"{OtlpCollectorEndpointSettings.SectionName}:GrpcPort and HttpPort must be different.");

        return settings;
    }

    public static string BuildRequireHostPattern(this OtlpCollectorEndpointSettings settings, int port)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Host switch
        {
            "0.0.0.0" => $"*:{port}",
            "*" => $"*:{port}",
            "+" => $"*:{port}",
            _ => $"{settings.Host}:{port}"
        };
    }

    public static void ConfigureListener(
        this KestrelServerOptions options,
        string host,
        int port,
        HttpProtocols protocols,
        Action<ListenOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException($"{OtlpCollectorEndpointSettings.SectionName}:Host is required.");

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenLocalhost(port, listen =>
            {
                listen.Protocols = protocols;
                configure?.Invoke(listen);
            });
            return;
        }

        if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenAnyIP(port, listen =>
            {
                listen.Protocols = protocols;
                configure?.Invoke(listen);
            });
            return;
        }

        if (!IPAddress.TryParse(host, out var ipAddress))
            throw new InvalidOperationException($"{OtlpCollectorEndpointSettings.SectionName}:Host '{host}' is not a supported IP address or 'localhost'.");

        options.Listen(ipAddress, port, listen =>
        {
            listen.Protocols = protocols;
            configure?.Invoke(listen);
        });
    }
}

