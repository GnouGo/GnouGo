# GnOuGo.Mcp.Core

GnOuGo-owned MCP servers use stable `ModelContextProtocol` `2.0.0` and leave protocol selection unpinned. The SDK prefers discovery-first `2026-07-28` (`GnOuGoMcpProtocol.PreferredRevision`) and accepts stable `2025-11-25` initialization as a compatibility fallback. `RequiredRevision` remains an obsolete source-compatible alias for the preferred revision.

Shared helpers for GnOuGo MCP servers.

## Artifact metadata contract

MCP tools can describe reusable, externally materialized artifacts through
`tools/list` metadata at `_meta.gnougo.artifacts`. Version `1` supports
`produces` entries with `kind`, structured-output instance `pointer`, and
`mode: materialize`, plus `consumes` entries with `kind`, input instance
`pointer`, and `required`. `McpArtifactContractParser` validates those pointers
against the advertised input/output schemas. `workspace.directory` is the
standard kind for a validated workspace-relative working directory.

The metadata is planning information, not authorization. Producing and
consuming MCP tools must still validate paths and policies at execution time.

## MCP protocol compatibility

The library targets the stable C# MCP SDK `2.0.0` and is shared by both MCP `2026-07-28` servers and down-level connections negotiated by the SDK. It does not add Tasks, MCP Apps, Roots, Sampling, or MCP Logging dependencies.

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
