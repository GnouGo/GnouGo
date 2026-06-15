# GnOuGo.Mcp.Core

Shared helpers for GnOuGo MCP servers.

## Build

```bash
dotnet build src/GnOuGo.Mcp.Core/GnOuGo.Mcp.Core.csproj
```

## Test

```bash
dotnet test tests/GnOuGo.Mcp.Core.Tests/GnOuGo.Mcp.Core.Tests.csproj
```

## Usage

Register the GnOuGo tool-error normalizer in an MCP server options block:

```csharp
services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "GnOuGo.Example.Mcp",
            Version = "1.0.0"
        };
        options.AddGnOuGoToolErrorNormalizer();
    });
```

The normalizer marks a `CallToolResult` as `IsError = true` when the returned
payload has a clear structured failure envelope, such as `success: false`,
`ok: false`, `status: "error"`, `error_code`, `error_message`, or a compact
`{ code, message }` error object. Plain text diagnostics are not treated as
errors.
