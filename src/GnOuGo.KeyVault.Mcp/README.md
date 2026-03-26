# GnOuGo.KeyVault.Mcp

`GnOuGo.KeyVault.Mcp` is a **stdio** MCP server that exposes the GnOuGo KeyVault secret manager as a set of MCP tools. It depends on `GnOuGo.KeyVault.Core` and uses SQLite for persistence with hybrid RSA + AES-GCM encryption.

> **Security note:** secret values are **never returned** by any tool. The `keyvault_set_secret` tool stores secrets but only echoes back metadata (id, key, version, tenant, timestamp).

## Exposed Tools

| Tool | Description |
|------|-------------|
| `keyvault_list_tenants` | Lists all active tenants. |
| `keyvault_create_tenant` | Creates a tenant with a dedicated RSA key pair. |
| `keyvault_delete_tenant` | Soft-deletes a tenant. |
| `keyvault_set_secret` | Creates or updates an encrypted secret (write-only, returns metadata). |
| `keyvault_list_secrets` | Lists secret metadata (key, tenant, version) without values. |
| `keyvault_delete_secret` | Soft-deletes a secret. |
| `keyvault_get_secret_versions` | Returns version history of a secret without values. |
| `keyvault_get_audit_log` | Returns the audit trail with optional filters and pagination. |

## Build

```bash
dotnet build src/GnOuGo.KeyVault.Mcp
```

## Run

```bash
dotnet run --project src/GnOuGo.KeyVault.Mcp
```

The server communicates over **stdin/stdout** using the MCP JSON-RPC protocol. All diagnostic logs are emitted to **stderr**.

## Configuration

Configuration is loaded from `appsettings.json` (copied next to the binary):

```json
{
  "KeyVault": {
    "DatabasePath": "data/gnougo-keyvault.db"
  }
}
```

- `DatabasePath` — relative (to the binary) or absolute path to the SQLite database. The directory is created automatically.

## MCP Client Integration

Example configuration for an MCP client (e.g., Claude Desktop, GnOuGo.Flow):

```json
{
  "mcpServers": {
    "keyvault": {
      "command": "dotnet",
      "args": ["run", "--project", "src/GnOuGo.KeyVault.Mcp"]
    }
  }
}
```

## Tests

Unit tests for the underlying service live in `tests/GnOuGo.KeyVault.Tests/`.

