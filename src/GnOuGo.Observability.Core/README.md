# GnOuGo.Observability.Core

Shared OpenTelemetry helpers for GnOuGo libraries, APIs, CLIs, and MCP servers.

## Features

- Binds the common `OpenTelemetry` configuration section.
- Configures OTLP traces, metrics, and logs.
- Supports gRPC and HTTP/protobuf exporters.
- Adds service/resource attributes consistently.
- Keeps stdio MCP hosts safe by not adding stdout logging providers.

## Configuration

```json
{
  "OpenTelemetry": {
    "Enabled": true,
    "ServiceName": "GnOuGo.Cmd.Mcp",
    "ServiceVersion": "1.0.0",
    "OtlpEndpoint": "http://127.0.0.1:4317",
    "Protocol": "Grpc",
    "TenantId": null,
    "IncludeLogs": true,
    "IncludeMetrics": true,
    "IncludeHttpClientInstrumentation": true,
    "IncludeAspNetCoreTraces": false
  }
}
```

## Build

```powershell
dotnet build .\src\GnOuGo.Observability.Core\GnOuGo.Observability.Core.csproj
```

## Usage

```csharp
using GnOuGo.Observability.Core;

builder.AddGnOuGoOpenTelemetry("GnOuGo.Cmd.Mcp");
```

