using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core;

/// <summary>
/// Creates HttpClient instances with optional SSL diagnostics and certificate bypass.
/// </summary>
public static class LLMHttpClientFactory
{
    /// <summary>
    /// Creates an HttpClient configured for LLM provider calls.
    /// When <paramref name="dangerousAcceptAnyCert"/> is true, all certificate errors are accepted (corporate proxy use case).
    /// SSL errors are always logged regardless of the flag.
    /// </summary>
    public static HttpClient Create(
        bool dangerousAcceptAnyCert = false,
        TimeSpan? timeout = null,
        ILogger? logger = null)
    {
        var effectiveLogger = logger ?? NullLogger.Instance;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors != SslPolicyErrors.None)
                    {
                        var certSubject = certificate?.Subject ?? "(no cert)";
                        var message = $"SSL certificate error: {sslPolicyErrors} for subject='{certSubject}'";

                        if (chain != null)
                        {
                            foreach (var status in chain.ChainStatus)
                                message += $" | ChainStatus: {status.Status} - {status.StatusInformation}";
                        }

                        effectiveLogger.LogWarning(message);

                        return dangerousAcceptAnyCert;
                    }

                    return true;
                }
            }
        };

        var http = new HttpClient(handler)
        {
            Timeout = timeout ?? LLMHttpClientDefaults.MinimumTimeout,
            DefaultRequestVersion = System.Net.HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        return http;
    }
}
