using System.Net;
using System.Net.Sockets;

namespace GnOuGo.Agent.Server.Tests;

internal static class TelemetryTestHostArgs
{
    public static string[] Create(params string[] extraArgs)
    {
        var grpcPort = GetFreePort();
        var httpPort = GetFreePort();
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
            $"--DocsIngestorMcp:DatabasePath={Path.Combine(baseDir, "docs-ingestor-metadata.db")}",
            $"--DocsIngestorMcp:VectorDatabasePath={Path.Combine(baseDir, "docs-ingestor-vectors.sqlite")}",
            $"--DocsIngestorMcp:OriginalsDirectory={Path.Combine(baseDir, "docs-ingestor", "originals")}",
            .. extraArgs
        ];
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

