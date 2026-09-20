# GnOuGo.Agent.Desktop

Photino desktop shell hosting GnOuGo.Agent.Server and its workflow designer. The embedded server uses the same KeyVault-backed model configuration and deterministic typed planner as the standalone server.

```sh
dotnet build src/GnOuGo.Agent.Desktop -m:1 -warnaserror
dotnet run --project src/GnOuGo.Agent.Desktop --no-build
```

Build the Agent.Server frontend with the repository's pnpm workspace before the first run. Development builds stage the bundled MCP tools beside the executable.

Databases resolve through GnOuGo.Workspace under the shared workspace. Telemetry initialization preserves existing data, including in development mode and when another host has the database open. Development mode makes tenant IDs optional for telemetry; it does not reset storage. The shell chooses an available application HTTP port; embedded OTLP ports remain configurable through Agent.Server settings.

If another host already owns OTLP ports 4317/4318, launch Desktop with distinct collector
ports and matching exporter/trace URLs. Check ownership with `lsof -nP -iTCP:4317 -sTCP:LISTEN`
on macOS; keep any host whose work needs to be preserved.

```sh
dotnet run --project src/GnOuGo.Agent.Desktop --no-build -- \
  --OtlpCollector:GrpcPort=14317 \
  --OtlpCollector:HttpPort=14318 \
  --OpenTelemetry:OtlpEndpoint=http://127.0.0.1:14317 \
  --TraceDebug:BaseUrl=http://127.0.0.1:14318
```

Choose free ports and retain these arguments on subsequent launches. Opening the executable
without arguments restores the configured defaults. For isolated tests, also retain the same
database paths, disabled planning background processing and provider protocol setting;
changing the telemetry ports alone does not select those settings. See the
[Desktop validation report](../../docs/planning-desktop-protocol-2026-09-20.md).

Validate the server and telemetry components separately:

```sh
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.OtlpCollector.Server.Tests -m:1 -warnaserror
```
