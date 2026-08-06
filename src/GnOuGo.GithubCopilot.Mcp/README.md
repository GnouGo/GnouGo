# GnOuGo.GithubCopilot.Mcp

MCP stdio server for safe code operations on a local project.

## MCP protocol compatibility

This stdio server uses the stable C# MCP SDK `2.0.0` and requires MCP `2026-07-28` for GnOuGo-owned peers. `GnOuGo.Flow.Core` leaves external connections unpinned, prefers `2026-07-28` discovery, and falls back to `2025-11-25` for older external servers. The GnOuGo JSONL progress stream remains a stderr side channel and does not alter the MCP wire contract.

## Features

- Inspect the active policy with `code_get_policy`.
- Summarize a project with `code_project_summary`.
- Read allowlisted text/code files with `code_read_file`.
- Search text with `code_search_text`.
- Ask GitHub Copilot for implementation guidance with `code_suggest_change` through `GitHub.Copilot.SDK`.
- Run GitHub Copilot in SDK agent mode with controlled local file edits via `code_agent_edit`.
- Emit structured GnOuGo progress events in real time on stderr and return them in `progressEvents`, so `GnOuGo.Flow.Core` can surface them as UI thinking/progress messages without depending on Copilot SDK event types.
- Optionally write files with `code_write_file` when `Code:AllowWrites=true`.
- Use the additive `copilot_*` tools backed by `GnOuGo.GithubCopilot.Core` for status/auth/models, explicit managed or one-shot sessions, foreground selection, messages/steering/queueing, safe history, abort, plans, modes/models, attachments, workspace files, skills/tool filtering, and stable elicitation callbacks.
- Run structured reviews with `copilot_review_start`, `copilot_review_analyze_batch`, `copilot_review_finish`, or the one-call `copilot_review` wrapper.
- Call `copilot_review_publication_gate` after re-reading the PR head SHA and before any GitHub write.

Git repository workflows are provided by the separate `GnOuGo.Git.Mcp` tool.

## Workspace path policy

Code and Copilot `projectRoot` and file paths may target normal visible content below the configured workspace. The `.GnOuGo/` subtree is reserved for GnOuGo-managed state and is rejected. Recursive project summaries, searches, and session file discovery omit that reserved tree. Workflow-owned checkouts should be materialized below `workflows/<purpose-specific-name>` by Git MCP and passed to later Code/Copilot calls through the returned project-root value.

## Authentication

`code_suggest_change` uses `GitHub.Copilot.SDK` and can authenticate with the locally signed-in GitHub user or with an explicit token when supported by the Copilot runtime.
Token resolution order is:

1. `Code:Copilot:ApiKey` in `appsettings.json` or another configuration provider.
2. Environment variables listed in `Code:Copilot:TokenEnvironmentVariables`, by default `GITHUB_TOKEN` then `COPILOT_API_KEY`.

For local desktop usage, prefer `Code:Copilot:UseLoggedInUser=true` and keep `ApiKey` empty. Do not commit real tokens.

Relevant Copilot settings:

- `Code:Copilot:Provider`: optional configured provider override. `Copilot` delegates to the current Agent default.
- `Code:Copilot:Model`: fallback model passed to the SDK session, default `gpt-5.4-mini` in the packaged configuration.
- `Code:Copilot:Mode`: Copilot mode, one of `ask`, `edit`, or `agent`; legacy `plan` is accepted as an alias for `ask`.
- `Code:Copilot:ReasoningEffort`: optional reasoning effort, default `high`.
- `Code:Copilot:UseLoggedInUser`: whether the SDK may use an already logged-in user when no explicit token is provided, default `false` in code defaults and `true` in the local appsettings template.
- `Code:Copilot:RequestTimeoutSeconds`: wait timeout for a Copilot response, default `120`.
- `Code:Copilot:ManagedSessionTtlSeconds`: inactivity TTL for an opaque managed handle, default `1800`.
- `Code:Copilot:EnableApproveAll`: host gate for `approve_all`, default `false`.

MCP transport sessions are never used as Copilot session identity. Managed calls use a `cps_*` opaque handle bound to `TenantId`; one-shot calls create, execute, disconnect, and permanently delete one SDK session. Request `_meta.gnougo` propagates tenant, correlation, run, step, repository, PR number, and head SHA.

Interactive permission, user-input, and nested MCP elicitation callbacks are bridged through stable MCP form elicitation. `deny` is appropriate for pure review inference. `auto_approve_allowlist` permits only explicitly named read-only paths/tools. `approve_all` is rejected unless the host gate is enabled.
Interactive permission prompts show the exact operation, warnings, sandbox-bypass status, and remembered scope. They offer **Allow once**, **Refuse**, and **Allow similar operations for this session** only when the SDK marks a matching scope as safe. Session decisions are scoped to command identifiers, exact MCP tools, URL domains, exact custom tools, or supported filesystem categories and are cleared when the managed session is deleted. Unrestricted `AllowAllForThisSession`, permanent approvals, and location-level approvals are not exposed.

When hosted by Agent Server/Desktop, `/mcp edit GnOuGo.GithubCopilot.Mcp` edits only provider, fallback model, reasoning effort, logged-in-user authentication, request timeout, and managed-session TTL. Every override is encrypted separately under `LLM--McpServerOverrides--GnOuGo.GithubCopilot.Mcp--...` and injected into the next MCP process through `Code__Copilot__...` environment keys. Selecting a field under **inherit fields** deletes only that override. Command, arguments, roots, extensions, write policy, credentials, and `EnableApproveAll` remain protected. Provider credentials continue to resolve from `LLM--Models--<provider>` and are never copied into the MCP override entries.

Provider resolution order is an explicit tool argument, the MCP provider override, then the Agent default. Model resolution order is the selected provider's KeyVault model, the MCP fallback model, then the packaged fallback. An existing managed session keeps the configuration captured when it was created; editor changes apply to the next workflow/MCP process.

## Pull-request review contract

Git MCP supplies exact patches. `copilot_review_start` and `copilot_review` accept optional `reviewInstructions` (maximum 32,000 characters) and `existingCommentsJson`. Existing comments contain path, optional side/line range, body, and optional fingerprint; only comments relevant to the current batch are included as bounded untrusted model context. Copilot review results contain fingerprint, severity, category, confidence, path, diff side, line range, evidence, explanation, and optional suggested patch. The server rejects unknown paths and lines outside the supplied diff, removes matching fingerprints or equivalent location/body findings, and reports binary/submodule skips plus truncated files.

The review session has no tools, no configuration discovery, no write permission, and is deleted after completion. Publication is deliberately outside this MCP and belongs to the Flow agent plus the official GitHub MCP. The publication gate fails closed for stale SHAs, dry runs, no findings, and unapproved interactive runs. `auto_comment` can only submit `COMMENT`; automated approval and merge are unsupported.

`code_suggest_change` and `code_agent_edit` also accept an optional `provider` parameter. When omitted, the default GitHub Copilot SDK behavior above is unchanged.
When provided, the MCP reads the matching provider from configuration and/or the shared GnOuGo KeyVault database, then passes it as a custom Copilot SDK provider for that call. KeyVault reads use a small SQLite/decryption helper from `GnOuGo.KeyVault.Core` instead of constructing the EF Core model inside the Native AOT stdio executable.
Supported provider section names are:

- `Code:Copilot:Providers:<provider>`
- compatibility fallback: `Code:Copilot:Providers:LLM--Models--<provider>`
- legacy fallback: `Code:Copilot:Providers:gnougo_llm_<provider>`

The section must contain at least `url`; `model` is recommended and falls back to `Code:Copilot:Model` when omitted. Supported provider fields include `type`, `wireApi`, `wireModel`, `authType`, `apiKey`, `bearerToken`, and OIDC fields such as `oidcIssuer`, `oidcClientId`, `oidcScopes`, `oidcClientSecret`, or `oidcPrivateKeyPem`. Keep secret values in environment variables or another secure configuration provider; do not commit real tokens to `appsettings.json`.

For local Agent/Desktop usage, LLM provider secrets saved by `/llm add` are stored in KeyVault with keys such as `LLM--Models--OpenAi` and legacy `gnougo_llm_OpenAi`. This MCP resolves those same default-tenant KeyVault secrets from `KeyVault:DatabasePath` (default `.GnOuGo/data/gnougo-keyvault.db`, mapped by `KeyVaultDatabasePathResolver` under the default `Desktop/GnOuGo` workspace). If both configuration and KeyVault define a provider, non-empty configuration values override KeyVault values, while empty configuration values such as `apiKey = ""` allow the KeyVault secret to supply the key.

Anthropic providers with `provider`/`type` set to `anthropic` are supported as custom SDK providers. They map to SDK provider type `anthropic` and default `wireApi` to `messages`; API-key auth is passed through as `ApiKey` for the Anthropic Messages API. The legacy `claude` provider/type values are still accepted as compatibility aliases.
If the requested provider does not exist, the tool returns structured content with `success: false`, `ok: false`, `error_code`, and `error_message`.

## Structured Error Handling

Policy, input, provider, cancellation, and unexpected tool failures are returned in the advertised tool result type with `success: false`, `ok: false`, `error_code`, and `error_message`. The shared MCP normalizer remains registered as a fallback for transport/SDK error results.

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
dotnet test "C:\github\GnouGo\tests\GnOuGo.GithubCopilot.Core.Tests\GnOuGo.GithubCopilot.Core.Tests.csproj"
```

## Native AOT publish

The project is configured for Native AOT and trimming analysis. `IL2026`, `IL3050`, `IL3053`, and `IL3055` are treated as build errors. Publish a self-contained native binary for a runtime identifier with:

```powershell
dotnet publish "C:\github\GnouGo\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:InvariantGlobalization=false -p:SkipModelMetadataGeneration=true
```

CI validates a dedicated `win-x64` Native AOT publish for `GnOuGo.GithubCopilot.Mcp` in `.github/workflows/build-agent-desktop-trimmed.yml`.
