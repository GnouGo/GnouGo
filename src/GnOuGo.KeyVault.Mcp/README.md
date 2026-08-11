# GnOuGo.KeyVault.Mcp

HTTP-based MCP server for encrypted KeyVault secret management.

## MCP protocol compatibility

This server uses the stable C# MCP SDK `2.0.0` and targets MCP `2026-07-28`. Its Streamable HTTP transport is explicitly stateless. Discovery-first clients use `server/discover`, while the SDK continues to accept older clients that negotiate a legacy protocol version.

## Architecture

This component is independently publishable, testable, and deployable per `AGENTS.md` rules.
It can run as a standalone HTTP MCP host or be mounted by any compatible ASP.NET Core host.
Persistence is handled by `KeyVaultService` from `GnOuGo.KeyVault.Core` using Entity Framework Core
(`KeyVaultDbContext`) with `AsNoTracking` optimizations for read queries.

## Hosted tools

- `keyvault_list_tenants`
- `keyvault_create_tenant`
- `keyvault_list_secrets`
- `keyvault_set_secret`
- `keyvault_get_secret`
- `keyvault_delete_secret`

This MCP surface stays intentionally narrow. Tenant deletion, audit log access, and secret version history remain outside this MCP contract.

## Structured Error Handling

All hosted tools return structured content. Failures use the shared GnOuGo MCP shape with `success: false`, `ok: false`, `error_code`, and `error_message`; older clients can still read the `error` field.

## Configuration

`appsettings.json`

```json
{
  "KeyVault": {
    "DatabasePath": ".GnOuGo/data/gnougo-keyvault.db"
  }
}
```

When `KeyVault:DatabasePath` keeps its default logical value (`.GnOuGo/data/gnougo-keyvault.db`), the actual SQLite file is created under the default workspace in `Desktop/GnOuGo/.GnOuGo/data/gnougo-keyvault.db`.
Explicit absolute paths are still honored.

## HTTP routes

### Standalone host (`GnOuGo.KeyVault.Mcp`)

By default, the standalone host exposes MCP over HTTP under:

- `/mcp`
- development URL: `http://127.0.0.1:5197/mcp`

Consumer example:

```json
{
  "Type": "http",
  "Url": "http://127.0.0.1:5197/mcp"
}
```

### Mounted by another host

Compatible hosts may mount the KeyVault MCP surface in-process at their own route, for example:

- `/mcp/keyvault`

The mounting host owns its route and service-discovery configuration. KeyVault does not depend on
the host's settings types, namespaces, or MCP catalog schema.

```json
{
  "Type": "http",
  "Url": "http://127.0.0.1:5000/mcp/keyvault"
}
```

## Run

```powershell
Set-Location "C:\github\GnouGo\src\GnOuGo.KeyVault.Mcp"
dotnet run
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.KeyVault.Mcp.Tests\GnOuGo.KeyVault.Mcp.Tests.csproj"
```

## Publish (self-contained trimmed)

```powershell
dotnet publish "C:\github\GnouGo\src\GnOuGo.KeyVault.Mcp\GnOuGo.KeyVault.Mcp.csproj" -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true
```
