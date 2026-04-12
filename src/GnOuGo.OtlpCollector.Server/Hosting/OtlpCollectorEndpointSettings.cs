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
        => BuildClientEndpoint(GrpcPort);

    public Uri GetHttpEndpoint()
        => BuildClientEndpoint(HttpPort);

    public string GetClientHost()
        => NormalizeClientHost(Host);

    internal static string NormalizeClientHost(string? host)
    {
        var normalizedHost = UnwrapHost(host);

        if (IsWildcardOrUnspecifiedHost(normalizedHost))
            return "127.0.0.1";

        return normalizedHost;
    }

    internal static bool IsWildcardOrUnspecifiedHost(string? host)
    {
        var normalizedHost = UnwrapHost(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return false;

        if (normalizedHost is "0.0.0.0" or "*" or "+" or "::" or "::0")
            return true;

        return IPAddress.TryParse(normalizedHost, out var ipAddress)
               && (IPAddress.Any.Equals(ipAddress) || IPAddress.IPv6Any.Equals(ipAddress));
    }

    internal static string UnwrapHost(string? host)
        => (host ?? string.Empty).Trim().Trim('[', ']');

    private Uri BuildClientEndpoint(int port)
        => new UriBuilder(Uri.UriSchemeHttp, GetClientHost(), port).Uri;
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

        if (OtlpCollectorEndpointSettings.IsWildcardOrUnspecifiedHost(settings.Host))
            return $"*:{port}";

        var host = OtlpCollectorEndpointSettings.UnwrapHost(settings.Host);
        return IPAddress.TryParse(host, out var ipAddress) && ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
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

        host = OtlpCollectorEndpointSettings.UnwrapHost(host);

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            options.ListenLocalhost(port, listen =>
            {
                listen.Protocols = protocols;
                configure?.Invoke(listen);
            });
            return;
        }

        if (OtlpCollectorEndpointSettings.IsWildcardOrUnspecifiedHost(host))
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

