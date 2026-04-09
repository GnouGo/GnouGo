# GnOuGo.KeyVault.Mcp

HTTP-based MCP server for encrypted KeyVault secret management.

## Architecture

This component is independently publishable, testable, and deployable per `AGENTS.md` rules.
It can run as a standalone HTTP MCP host or be mounted inside `GnOuGo.Agent.Server`.

## Hosted tools

- `keyvault_list_tenants`
- `keyvault_create_tenant`
- `keyvault_list_secrets`
- `keyvault_set_secret`
- `keyvault_get_secret`
- `keyvault_delete_secret`

This MCP surface stays intentionally narrow. Tenant deletion, audit log access, and secret version history remain outside this MCP contract.

## Configuration

`appsettings.json`

```json
{
  "KeyVault": {
    "DatabasePath": "data/gnougo-keyvault.db"
  }
}
```

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

### Mounted inside `GnOuGo.Agent.Server`

When the local agent server hosts the KeyVault MCP surface in-process, it is mounted at:

- `/mcp/keyvault`

Default `GnOuGo.Agent.Server/appsettings.json` placeholder:

```json
{
  "LLM": {
    "McpServers": {
      "GnOuGo.KeyVault.Mcp": {
        "Type": "http",
        "Url": "http://127.0.0.1:0/mcp/keyvault"
      }
    }
  }
}
```

At runtime, `GnOuGo.Agent.Server` replaces port `0` with the actual local listening port.

## Run

```powershell
Set-Location "C:\github\GnouGo\src\GnOuGo.KeyVault.Mcp"
dotnet run
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.KeyVault.Mcp.Tests\GnOuGo.KeyVault.Mcp.Tests.csproj"
```


