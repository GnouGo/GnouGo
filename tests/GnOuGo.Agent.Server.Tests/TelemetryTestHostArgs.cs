using System.Net;
using System.Net.Sockets;
using GnOuGo.Agent.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.Agent.Server.Tests;

internal static class TelemetryTestHostArgs
{
    public static WebApplication Build(
        string[] args,
        string? urls = null,
        string? contentRoot = null,
        bool enableHttpsRedirection = true,
        Action<IServiceCollection>? configureServices = null)
    {
        var sockets = new CollectorSockets();
        try
        {
            var app = GnOuGoAgentWebHost.Build(
                [.. args, $"--OtlpCollector:GrpcPort={sockets.GrpcPort}", $"--OtlpCollector:HttpPort={sockets.HttpPort}"],
                urls, contentRoot, enableHttpsRedirection, services =>
                {
                    configureServices?.Invoke(services);
                    // The container owns unclaimed sockets even when this app is never started.
                    services.AddSingleton(_ => sockets);
                    services.Configure<SocketTransportOptions>(options =>
                    {
                        var fallback = options.CreateBoundListenSocket;
                        options.CreateBoundListenSocket = endpoint => sockets.Take(endpoint) ?? fallback(endpoint);
                    });
                });
            _ = app.Services.GetRequiredService<CollectorSockets>();
            return app;
        }
        catch
        {
            sockets.Dispose();
            throw;
        }
    }

    public static string[] Create(params string[] extraArgs)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var baseDir = Path.Combine(Path.GetTempPath(), "gnougo-agent-server-tests", suffix);
        Directory.CreateDirectory(baseDir);

        return
        [
            "--DevMode:Enabled=true",
            "--TypedWorkflowPlanning:OpenLocalOperatorBrowser=false",
            "--OpenTelemetry:Enabled=true",
            "--Ingest:BatchSize=1",
            "--Ingest:FlushSeconds=1",
            "--OtlpCollector:Host=127.0.0.1",
            $"--Database:Path={Path.Combine(baseDir, "telemetry.db")}",
            $"--Agent:DatabasePath={Path.Combine(baseDir, "agent.db")}",
            $"--KeyVault:DatabasePath={Path.Combine(baseDir, "keyvault.db")}",
            $"--TypedWorkflowPlanning:DatabasePath={Path.Combine(baseDir, "planning.db")}",
            $"--DocsIngestorMcp:DatabasePath={Path.Combine(baseDir, "docs-ingestor-metadata.db")}",
            $"--DocsIngestorMcp:VectorDatabasePath={Path.Combine(baseDir, "docs-ingestor-vectors.sqlite")}",
            $"--DocsIngestorMcp:OriginalsDirectory={Path.Combine(baseDir, "docs-ingestor", "originals")}",
            .. extraArgs
        ];
    }

    // Bind port zero once and hand the same socket to Kestrel. Selecting a port and
    // closing its listener before host startup leaves a race with concurrent hosts.
    private sealed class CollectorSockets : IDisposable
    {
        private readonly Dictionary<EndPoint, Socket> owned = [];
        internal int GrpcPort { get; }
        internal int HttpPort { get; }

        internal CollectorSockets()
        {
            try
            {
                GrpcPort = Reserve();
                HttpPort = Reserve();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private int Reserve()
        {
            var socket = SocketTransportOptions.CreateDefaultBoundListenSocket(new IPEndPoint(IPAddress.Loopback, 0));
            try
            {
                socket.Listen(512);
                owned.Add(socket.LocalEndPoint!, socket);
                return ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal Socket? Take(EndPoint endpoint)
        {
            lock (owned)
                return owned.Remove(endpoint, out var socket) ? socket : null;
        }

        public void Dispose()
        {
            lock (owned)
            {
                foreach (var socket in owned.Values) socket.Dispose();
                owned.Clear();
            }
        }
    }
}
