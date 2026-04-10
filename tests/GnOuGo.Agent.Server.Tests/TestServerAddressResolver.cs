using System.Net;

namespace GnOuGo.Agent.Server.Tests;

internal static class TestServerAddressResolver
{
    public static string NormalizeBaseAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return address.TrimEnd('/');

        var host = NormalizeHost(uri.Host);
        if (string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase))
            return address.TrimEnd('/');

        var builder = new UriBuilder(uri)
        {
            Host = host
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string NormalizeHost(string? host)
    {
        var normalizedHost = (host ?? string.Empty).Trim().Trim('[', ']');
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return normalizedHost;

        if (normalizedHost is "0.0.0.0" or "*" or "+" or "::" or "::0")
            return "127.0.0.1";

        if (IPAddress.TryParse(normalizedHost, out var ipAddress)
            && (IPAddress.Any.Equals(ipAddress) || IPAddress.IPv6Any.Equals(ipAddress)))
        {
            return "127.0.0.1";
        }

        return normalizedHost;
    }
}

