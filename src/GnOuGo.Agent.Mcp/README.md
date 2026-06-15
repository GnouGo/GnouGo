# GnOuGo.Agent.Mcp

HTTP-based MCP server for agent data, chat history, and diff-related tools.

## Architecture

This component is independently publishable, testable, and deployable per `AGENTS.md` rules.
It can run as a standalone HTTP MCP host or be mounted inside `GnOuGo.Agent.Server`.

## Hosted tools

- `agent_add` — Create a new agent with name and workflow
- `agent_update` — Update an existing agent's name and workflow
- `agent_list` — List all agents
- `agent_delete` — Delete an agent by identifier
- `agent_get_by_name` — Get an agent by name (case-insensitive)
- `user_chat_history_append` — Append messages to a chat conversation
- `user_chat_history_get` — Retrieve chat history messages for a conversation
- `user_config_get` — Read persisted local user defaults (default LLM + default agent)
- `user_config_set` — Persist local user defaults (default LLM + default agent)

KeyVault tools now live in the dedicated HTTP project `GnOuGo.KeyVault.Mcp`.

## Configuration

`appsettings.json`

```json
{
  "Agent": {
    "DatabasePath": ".GnOuGo/data/gnougo-agent.db"
  }
}
```

Agent definitions are stored as YAML files in the GnOuGo workspace:

```text
<workspace>/.GnOuGo/{agent-name}.yaml
```

The agent list is obtained by enumerating `*.yaml` files from the hidden `.GnOuGo` workspace directory.
Agent names are validated as safe file names; absolute paths and path traversal are not accepted as names.

SQLite is still used for non-agent persisted data:

- diff revisions,
- persisted user defaults (`default_llm_provider`, `default_llm_model`, `default_agent`).

User configuration persistence is implemented with **Entity Framework Core** (`AgentMcpDbContext` + `DiffDbContext`).

When `Agent:DatabasePath` keeps its default logical value (`.GnOuGo/data/gnougo-agent.db`), the actual SQLite file is created under the default workspace in `Desktop/GnOuGo/.GnOuGo/data/gnougo-agent.db`.
Agent YAML files are created under the hidden workspace data directory, for example `Desktop/GnOuGo/.GnOuGo/demo.yaml`.
Explicit absolute paths are still honored.

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

## Publish (self-contained trimmed)

`GnOuGo.Agent.Mcp` publishes as a trimmed self-contained single-file executable.

```powershell
Set-Location "C:\github\GnouGo"
dotnet publish "src\GnOuGo.Agent.Mcp\GnOuGo.Agent.Mcp.csproj" -c Release -r win-x64 --self-contained true -p:PublishTrimmed=true -p:PublishSingleFile=true -o "artifacts\agent-mcp-win-x64"
```
