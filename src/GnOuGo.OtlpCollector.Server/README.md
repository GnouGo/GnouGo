# GnOuGo.OtlpCollector.Server

OTLP collector server for GnOuGo.

This service receives OpenTelemetry data (traces, metrics, logs) over OTLP/gRPC and OTLP/HTTP, stores telemetry in SQLite, and exposes a small admin API plus a static UI.

## Features

- OTLP ingest endpoints on ports `4317` (gRPC) and `4318` (HTTP)
- SQLite persistence (`data/telemetry.db` by default)
- Configurable batching, queue capacity, and retention sweep
- Optional routing to external OTLP/HTTP collectors using typed `TelemetryRouting` settings
- Development mode support when tenant id is missing
- Static UI served from `wwwroot`
- NativeAOT publish in `Release`; runtime persistence uses `Microsoft.Data.Sqlite` directly to avoid EF Core dynamic-code paths

## Prerequisites

- .NET SDK 10.0+
- Node.js 24+
- pnpm 10+

## Local development

### 1) Build the frontend

```bash
cd src/GnOuGo.OtlpCollector.Server/ClientApp
pnpm install --frozen-lockfile
pnpm build
```

### 2) Run the server

```bash
cd <repo-root>
dotnet run --project src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj
```

The server listens on:

- `http://0.0.0.0:4317` (OTLP gRPC)
- `http://0.0.0.0:4318` (OTLP HTTP + admin API + static UI)

## Configuration

Main settings are in `src/GnOuGo.OtlpCollector.Server/appsettings.json`:

- `Admin:Key`: admin key for protected operations
- `Database:Path`: SQLite file path
- `Ingest:BatchSize`: flush batch size
- `Ingest:FlushSeconds`: periodic flush interval
- `Ingest:ChannelCapacity`: ingest queue capacity
- `Retention:SweepSeconds`: retention sweep interval
- `TelemetryRouting`: optional OTLP/HTTP forwarding rules and collector destinations
- `DevMode:Enabled`: allow missing tenant id in development
- `Kestrel:Endpoints`: OTLP/http listen addresses and protocols

## Telemetry routing

`TelemetryRouting` can forward flushed telemetry to one of several OTLP/HTTP collectors after the local SQLite write succeeds.

The default example models this routing order:

1. matching name/value filters go to collector `C`;
2. RAG / GenAI workflow traces go to collector `A`;
3. all other traces/logs go to collector `B` through `DefaultCollector`.

The router buffers trace-linked rows briefly by `TraceId` (`TraceBufferSeconds`) so that if any span/log in the trace matches the RAG rule, the whole trace batch is forwarded to collector `A`.

Example:

```json
{
  "TelemetryRouting": {
	"Enabled": true,
	"TraceBufferSeconds": 2,
	"DefaultCollector": "B",
	"Collectors": {
	  "A": { "Endpoint": "http://collector-a:4318" },
	  "B": { "Endpoint": "http://collector-b:4318" },
	  "C": { "Endpoint": "http://collector-c:4318" }
	},
	"Rules": [
	  {
		"Name": "filtered-workflows-to-c",
		"Enabled": true,
		"Collector": "C",
		"Signals": [ "traces", "logs" ],
		"MatchAny": [
		  { "SpanNameContains": [ "important-operation" ] },
		  {
			"AnyAttributes": [
			  { "Key": "workflow.name", "Contains": "important-workflow" }
			]
		  }
		]
	  },
	  {
		"Name": "rag-genai-workflows-to-a",
		"Enabled": true,
		"Collector": "A",
		"Signals": [ "traces", "logs" ],
		"MatchAny": [
		  { "AnyAttributes": [ { "Key": "workflow.type", "Value": "rag" } ] },
		  { "AnyAttributes": [ { "Key": "gen_ai.operation.name", "Contains": "rag" } ] },
		  { "SpanNameContains": [ "rag", "retrieval", "embedding", "vector" ] }
		]
	  }
	]
  }
}
```

Collector endpoints are OTLP/HTTP base URLs. The forwarder posts protobuf payloads to `/v1/traces` and `/v1/logs`. When `IncludeTenantHeader` is true, it forwards the tenant as `x-tenant-id`.

Avoid storing production secrets in `appsettings.json`; use environment-specific configuration or a secret provider for sensitive headers.

## Admin endpoints

- `GET /admin/queue-status`
- `GET /admin/config`

## Publish

`Release` publishes are NativeAOT by default. Example Windows x64 publish:

```powershell
dotnet publish src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/otlp-collector/win-x64 /p:SkipClientBuild=true /p:SkipModelMetadataGeneration=true
```

Example macOS arm64 publish:

```powershell
dotnet publish src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/publish/otlp-collector/osx-arm64 /p:SkipClientBuild=true /p:SkipModelMetadataGeneration=true
```

To explicitly disable NativeAOT for diagnostics:

```powershell
dotnet publish src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/otlp-collector/win-x64-il /p:PublishAot=false /p:PublishTrimmed=false /p:SkipClientBuild=true
```

## CI artifacts

GitHub Actions publishes downloadable zip artifacts for:

- `linux-x64`
- `win-x64`
- `osx-arm64`

Artifact name format:

- `GnOuGo.OtlpCollector.Server-<rid>.zip`


