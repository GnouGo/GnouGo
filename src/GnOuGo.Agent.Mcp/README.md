# GnOuGo.Agent.Mcp

HTTP-based MCP server for agent data, chat history, and diff-related tools.

## Hosted tool domains

- Agent management tools
- Chat history tools
- Data and diff helpers

KeyVault tools now live in the dedicated HTTP project `GnOuGo.KeyVault.Mcp`.

## Configuration

`appsettings.json`

```json
{
  "Agent": {
    "DatabasePath": "data/gnougo-agent.db"
  }
}
```

## HTTP routes

### Standalone host (`GnOuGo.Agent.Mcp`)

The standalone MCP host maps the protocol endpoint at:

- `/mcp`
- development URL: `http://127.0.0.1:5198/mcp`

Consumer example:

```json
{
  "Type": "http",
  "Url": "http://127.0.0.1:5198/mcp"
}
```

### Mounted inside `GnOuGo.Agent.Server`

When the Blazor agent server hosts the MCP services in-process, the same tools are mounted at:

- `/mcp/agent`

Default `GnOuGo.Agent.Server/appsettings.json` placeholder:

```json
{
  "LLM": {
    "McpServers": {
      "GnOuGo.Agent.Mcp": {
        "Type": "http",
        "Url": "http://127.0.0.1:0/mcp/agent"
      }
    }
  }
}
```

At runtime, `GnOuGo.Agent.Server` replaces port `0` with the actual local listening port.

## Run

```powershell
Set-Location "C:\github\GnouGo\src\GnOuGo.Agent.Mcp"
dotnet run
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.Agent.Mcp.Tests\GnOuGo.Agent.Mcp.Tests.csproj"
```

