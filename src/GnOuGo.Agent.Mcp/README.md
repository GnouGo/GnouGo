# GnOuGo.Agent.Mcp

HTTP-based MCP server for agent data, chat history, and diff-related tools.

## Architecture

This component is independently publishable, testable, and deployable per `AGENTS.md` rules.
It can run as a standalone HTTP MCP host or be mounted inside `GnOuGo.Agent.Server`.

## Hosted tools

- `agent_add` — Create a new agent with name, workflow, and optional schedules
- `agent_update` — Update an existing agent's name, workflow, and schedules
- `agent_list` — List all agents
- `agent_delete` — Delete an agent by identifier
- `agent_get_by_name` — Get an agent by name (case-insensitive)
- `user_chat_history_append` — Append messages to a chat conversation
- `user_chat_history_get` — Retrieve chat history messages for a conversation

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

