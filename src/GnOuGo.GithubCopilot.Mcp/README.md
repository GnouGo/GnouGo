# GnOuGo.GithubCopilot.Mcp

MCP stdio server for safe code operations on a local project.

## Features

- Inspect the active policy with `code_get_policy`.
- Summarize a project with `code_project_summary`.
- Read allowlisted text/code files with `code_read_file`.
- Search text with `code_search_text`.
- Ask GitHub Copilot for implementation guidance with `code_suggest_change` through `GitHub.Copilot.SDK`.
- Run GitHub Copilot in SDK agent mode with controlled local file edits via `code_agent_edit`.
- Emit structured GnOuGo progress events in real time on stderr and return them in `progressEvents`, so `GnOuGo.Flow.Core` can surface them as UI thinking/progress messages without depending on Copilot SDK event types.
- Optionally write files with `code_write_file` when `Code:AllowWrites=true`.

Git repository workflows are provided by the separate `GnOuGo.Git.Mcp` tool.

## Authentication

`code_suggest_change` uses `GitHub.Copilot.SDK` and can authenticate with the locally signed-in GitHub user or with an explicit token when supported by the Copilot runtime.
Token resolution order is:

1. `Code:Copilot:ApiKey` in `appsettings.json` or another configuration provider.
2. Environment variables listed in `Code:Copilot:TokenEnvironmentVariables`, by default `GITHUB_TOKEN` then `COPILOT_API_KEY`.

For local desktop usage, prefer `Code:Copilot:UseLoggedInUser=true` and keep `ApiKey` empty. Do not commit real tokens.

Relevant Copilot settings:

- `Code:Copilot:Model`: model passed to the SDK session, default `gpt-4.1`.
- `Code:Copilot:Mode`: Copilot mode, one of `ask`, `edit`, or `agent`; legacy `plan` is accepted as an alias for `ask`.
- `Code:Copilot:ReasoningEffort`: optional reasoning effort, default `high`.
- `Code:Copilot:UseLoggedInUser`: whether the SDK may use an already logged-in user when no explicit token is provided, default `false` in code defaults and `true` in the local appsettings template.
- `Code:Copilot:RequestTimeoutSeconds`: wait timeout for a Copilot response, default `120`.

`code_suggest_change` also accepts an optional `provider` parameter. When omitted, the default GitHub Copilot SDK behavior above is unchanged.
When provided, the MCP reads the matching Agent Server LLM provider secret from the shared KeyVault database and passes it as a custom Copilot SDK provider for that call.
Supported KeyVault secret names follow the Agent Server conventions:

- `LLM--Models--<provider>`
- legacy fallback: `gnougo_llm_<provider>`

The secret value must be a JSON object containing at least `url`; `model` is recommended and falls back to `Code:Copilot:Model` when omitted. Supported provider fields include `type`, `wireApi`, `wireModel`, `authType`, `apiKey`, `bearerToken`, and OIDC fields such as `oidcIssuer`, `oidcClientId`, `oidcScopes`, `oidcClientSecret`, or `oidcPrivateKeyPem`.

Anthropic providers saved by Agent Server `/llm add` are supported as custom SDK providers. A KeyVault secret with `provider`/`type` set to `anthropic` is mapped to SDK provider type `anthropic` and defaults `wireApi` to `messages`; API-key auth is passed through as `ApiKey` for the Anthropic Messages API. The legacy `claude` provider/type values are still accepted as compatibility aliases.
If the requested provider does not exist, the tool returns a standard MCP tool error.

## Agent edit mode

`code_agent_edit` runs the GitHub Copilot SDK with `Mode=agent` and a local `SessionFsProvider` implementation.
This lets Copilot edit files directly through the MCP process while still enforcing the same project policy as manual file writes:

- `Code:AllowWrites` must be `true`.
- Paths must stay inside the resolved project root / allowed roots.
- File extensions must be allowlisted by `Code:AllowedExtensions`.
- Parent traversal and wildcard paths are rejected.

The older `code_suggest_change` tool remains suggestion-only and does not write files.

Both `code_suggest_change` and `code_agent_edit` emit progress milestones as structured JSONL stderr messages while the call is running, and include the same events in the final `progressEvents` array.

`progressEvents` is the official GnOuGo contract. Application milestones and native `GitHub.Copilot.SDK` session events are both normalized to this schema before they leave this MCP server. `GnOuGo.Flow.Core`, Agent Server, and the UI must consume this contract instead of coupling directly to SDK-specific event classes or payload shapes. When the SDK exposes useful complete events, this MCP maps them to stable `sdk_*` `kind` values; when it does not, the explicit GnOuGo milestones still provide progress.

Each item contains:

- `kind`: stable machine-readable phase, for example `prepare`, `provider`, `session_create`, `request_send`, `completed`, `file_modified`, or SDK-mapped phases such as `sdk_assistant_turn_start` and `sdk_tool_execution_progress`.
- `level`: UI hint such as `thinking` or `info`.
- `message`: user-facing progress text. This is an operational milestone, not raw model chain-of-thought. SDK reasoning/streaming deltas are not forwarded verbatim.
- `timestamp`: UTC event timestamp.
- `file`: optional relative file path for file-level events.

When called through `GnOuGo.Flow.Core` `mcp.call`, stderr progress events are forwarded immediately as `gnougo-flow.step.thinking` telemetry events and can be streamed by Agent Server. The final `progressEvents` array remains as a fallback/history in the tool result. The real-time stderr JSONL transport is a GnOuGo stdio side channel; the stable product contract remains the `progressEvents` schema above.

PowerShell example:

```powershell
dotnet run --project "C:\github\GnouGo\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj"
```

The first build may download the Copilot CLI binary through the `GitHub.Copilot.SDK` package targets.

## Build

```powershell
dotnet build "C:\github\GnouGo\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj" -p:SkipModelMetadataGeneration=true
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.GithubCopilot.Mcp.Tests\GnOuGo.GithubCopilot.Mcp.Tests.csproj" -p:SkipModelMetadataGeneration=true
```


