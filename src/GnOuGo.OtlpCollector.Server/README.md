# GnOuGo.OtlpCollector.Server

OTLP collector server for GnOuGo.

This service receives OpenTelemetry data (traces, metrics, logs) over OTLP/gRPC and OTLP/HTTP, stores telemetry in SQLite, and exposes a small admin API plus a static UI.

## Features

- OTLP ingest endpoints on ports `4317` (gRPC) and `4318` (HTTP)
- SQLite persistence (`data/telemetry.db` by default)
- Configurable batching, queue capacity, and retention sweep
- Development mode support when tenant id is missing
- Static UI served from `wwwroot`

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
- `DevMode:Enabled`: allow missing tenant id in development
- `Kestrel:Endpoints`: OTLP/http listen addresses and protocols

## Admin endpoints

- `GET /admin/queue-status`
- `GET /admin/config`

## Publish

Example self-contained publish:

```bash
cd <repo-root>
dotnet restore src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj -r osx-arm64 /p:SkipClientBuild=true
dotnet publish src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/publish/otlp-collector/osx-arm64 --no-restore /p:SkipClientBuild=true
```

## CI artifacts

GitHub Actions publishes downloadable zip artifacts for:

- `linux-x64`
- `win-x64`
- `osx-arm64`

Artifact name format:

- `GnOuGo.OtlpCollector.Server-<rid>.zip`


