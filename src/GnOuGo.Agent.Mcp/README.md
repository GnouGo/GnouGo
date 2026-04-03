# GnOuGo.Agent.Mcp

Stdio-based MCP server for agent data, chat history, diff-related tools, and KeyVault tools.

## Hosted tool domains

- Agent management tools
- Chat history tools
- KeyVault secret-management tools with direct latest-secret retrieval

## KeyVault MCP tools

The KeyVault MCP surface is intentionally small:

- list tenants
- create tenant
- list secrets
- set secret
- get secret (returns the latest decrypted secret value)
- delete secret

Tenant deletion, audit-log retrieval, and secret-version history remain available in `GnOuGo.KeyVault.Server`, but are not exposed through this MCP surface.

## Configuration

`appsettings.json`

```json
{
  "Agent": {
    "DatabasePath": "data/gnougo-agent.db"
  },
  "KeyVault": {
    "DatabasePath": "data/gnougo-keyvault.db"
  }
}
```

## Consumer configuration

Consumers should launch this MCP server over stdio, for example:

```json
{
  "Type": "stdio",
  "Command": "dotnet",
  "Args": [
    "run",
    "--project",
    "src/GnOuGo.Agent.Mcp/GnOuGo.Agent.Mcp.csproj"
  ]
}
```

## Run

```powershell
Set-Location "src/GnOuGo.Agent.Mcp"
dotnet run
```

## Test

```powershell
dotnet test "tests/GnOuGo.Agent.Mcp.Tests/GnOuGo.Agent.Mcp.Tests.csproj"
```

