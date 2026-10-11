using System.Net;
using System.Net.Sockets;

namespace GnOuGo.Agent.Server.Tests;

internal static class TelemetryTestHostArgs
{
    public static string[] Create(params string[] extraArgs)
    {
        // Hold both reservations until distinct ports have been selected. Releasing the
        // first listener before binding the second lets the OS return the same port.
        using var grpcListener = new TcpListener(IPAddress.Loopback, 0);
        using var httpListener = new TcpListener(IPAddress.Loopback, 0);
        grpcListener.Start();
        httpListener.Start();
        var grpcPort = ((IPEndPoint)grpcListener.LocalEndpoint).Port;
        var httpPort = ((IPEndPoint)httpListener.LocalEndpoint).Port;
        var suffix = Guid.NewGuid().ToString("N");
        var baseDir = Path.Combine(Path.GetTempPath(), "gnougo-agent-server-tests", suffix);
        Directory.CreateDirectory(baseDir);

        return
        [
            "--DevMode:Enabled=true",
            "--OpenTelemetry:Enabled=true",
            "--Ingest:BatchSize=1",
            "--Ingest:FlushSeconds=1",
            "--OtlpCollector:Host=127.0.0.1",
            $"--OtlpCollector:GrpcPort={grpcPort}",
            $"--OtlpCollector:HttpPort={httpPort}",
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

}
