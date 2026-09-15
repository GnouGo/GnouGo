# GnOuGo.Agent.Desktop

Photino desktop shell hosting GnOuGo.Agent.Server and its workflow designer. The embedded server uses the same KeyVault-backed model configuration and deterministic typed planner as the standalone server.

```sh
dotnet build src/GnOuGo.Agent.Desktop -m:1 -warnaserror
dotnet run --project src/GnOuGo.Agent.Desktop --no-build
```

Build the Agent.Server frontend with the repository's pnpm workspace before the first run. Development builds stage the bundled MCP tools beside the executable.

Databases resolve through GnOuGo.Workspace under the shared workspace. Telemetry initialization preserves existing data, including in development mode and when another host has the database open. Development mode makes tenant IDs optional for telemetry; it does not reset storage. The shell chooses an available application HTTP port; embedded OTLP ports remain configurable through Agent.Server settings.

Validate the server and telemetry components separately:

```sh
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.OtlpCollector.Server.Tests -m:1 -warnaserror
```
