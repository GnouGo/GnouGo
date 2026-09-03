# GnOuGo.GithubCopilot.Mcp

MCP stdio server for safe code operations on a local project.

## MCP protocol compatibility

This stdio server uses the stable C# MCP SDK `2.0.0` with automatic protocol negotiation: clients prefer `2026-07-28` discovery and can initialize with stable `2025-11-25`. Launch the built apphost, or use `dotnet GnOuGo.GithubCopilot.Mcp.dll`; do not put `dotnet run` on an MCP stdio transport because CLI output can corrupt the JSONL stream. The GnOuGo progress stream remains a stderr side channel and does not alter the MCP wire contract.

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

Code and Copilot `projectRoot` and file paths may target normal visible content below the configured workspace. The `.GnOuGo/` subtree is reserved for GnOuGo-managed state and is rejected. Recursive project summaries, searches, and session file discovery omit that reserved tree. Tools taking `projectRoot` advertise a required `workspace.directory` consumer contract and accept the exact validated workspace-relative value returned by any compatible MCP producer; they do not depend on a particular producer tool.

## Authentication

`code_suggest_change` uses `GitHub.Copilot.SDK` and can authenticate with the locally signed-in GitHub user or with an explicit token when supported by the Copilot runtime.
Token resolution order after all configuration overlays are applied is:

1. `Code:Copilot:ApiKey` in the effective typed settings.
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
- `Code:Copilot:EnableSandboxBypassGrants`: independent host gate for explicitly remembered sandbox-bypass approvals, default `false`.
- `Code:Copilot:WorkflowGrantTtlSeconds`: inactivity expiry for in-memory workflow-run grants, default `86400`.

MCP transport sessions are never used as Copilot session identity. Managed calls use a `cps_*` opaque handle bound to `TenantId`; the session-create tool advertises that handle as a materialized `session.handle` artifact and lifecycle consumers declare the matching required artifact. One-shot calls create, execute, disconnect, and permanently delete one SDK session, and advertise a complete-operation composition encapsulating those lower-level session phases so a provider-neutral planner can avoid redundant wrapper-plus-phase execution. Request `_meta.gnougo` propagates tenant, correlation, stable execution and agent identity, run, step, repository, PR number, and head SHA. The host owns the execution and agent fields; workflow inputs cannot override them.

Interactive permission, user-input, and nested MCP elicitation callbacks are bridged through stable MCP form elicitation. `copilot_session_create` publishes the managed-session permission enum and defaults to `interactive`. `copilot_one_shot` is deliberately non-interactive, publishes only `auto_approve_allowlist`, `deny`, and `approve_all`, and defaults to `deny`; its `permissionAllowlistJson` argument supplies the explicit allowlist when that mode is selected. Use `copilot_interactive_one_shot` for dependency installation, tests, linting, edits, or other one-turn work that may execute tools: it creates a managed interactive session and permanently deletes it after success, failure, or cancellation. `deny` is appropriate for pure review inference. `auto_approve_allowlist` permits only explicitly named read-only paths/tools. `approve_all` is rejected unless the host gate is enabled and must not be generated without explicit unattended intent and established host availability.
Interactive permission prompts show the exact operation, warnings, sandbox-bypass status, and remembered scope. They offer **Allow once**, **Refuse**, and **Allow similar operations for this task** only when the SDK marks a matching scope as safe. When `EnableApproveAll` is enabled, the same interactive callback may also offer **Allow all for this Copilot task**, **Allow all for this workflow run**, and **Allow all future runs for this agent** when the required stable identities are available. Ordinary broad grants never include sandbox bypass. When `EnableSandboxBypassGrants` is also enabled, a bypass request offers explicit task, workflow, and future-agent choices that include ordinary and bypass operations. Future-agent approval requires a second confirmation and is stored by tenant plus stable agent ID, so it follows renames and survives restarts. Workflow grants are tenant/run scoped and expire after inactivity.
Automatically reused permissions do not create an elicitation card. They emit redacted `permission.requested` and `permission.auto_approved` progress entries, including the sandbox-bypass flag, while explicit answers emit `permission.granted` or `permission.refused`. Grant revocation emits `permission.grant.revoked`. Raw model reasoning and likely credentials are never included.
Every interactive elicitation carries the originating `_meta.gnougo` correlation back to the Flow client. The active tool cancellation token is linked to the Copilot callback, so cancelling the workflow releases the pending permission request instead of leaving the stdio server waiting. Interactive one-shot progress includes session creation, request processing, permission requested/resolved, completion or cancellation, and session deletion; it never includes raw reasoning.

Future-agent permission grants are encrypted and stored through the storage-agnostic `IKeyVaultRecordStore` contract in `GnOuGo.KeyVault.Core`, using the tenant-isolated `github-copilot.permission-grants` collection. The MCP contains no SQL or storage-path logic. Workflow-run grants remain in memory and expire after inactivity. Management-only tools list and revoke future-agent grants; their `gnougo.management.visibility=management_only` metadata excludes them from ordinary workflow generation. Agent Server exposes them as `/mcp copilot permissions` and `/mcp copilot permissions revoke <grant-id>`. Deleting an agent revokes its persistent grants. Existing grants in the legacy `gnougo-copilot-permissions.db` file are not migrated or deleted; they require explicit approval again.

When hosted by Agent Server/Desktop, `/mcp edit GnOuGo.GithubCopilot.Mcp` edits provider, fallback model, reasoning effort, logged-in-user authentication, request timeout, managed-session TTL, broad approvals, and reusable sandbox-bypass approvals. Every override is encrypted separately under `LLM--McpServerOverrides--GnOuGo.GithubCopilot.Mcp--...`. Selecting a field under **inherit fields** deletes only that override. Enabling reusable sandbox bypass automatically enables broad approvals. Command, arguments, roots, extensions, write policy, and credentials remain protected. Provider credentials remain in `LLM--Models--<provider>` and are never copied into MCP-specific entries. Agent Server passes only `KeyVault__DatabasePath` to this direct-reader MCP; it never injects decrypted `Code__Copilot__...` values.

Provider resolution order is an explicit tool argument, the MCP provider override, then the Agent default. Model resolution order is the selected provider's KeyVault model, the MCP fallback model, then the packaged fallback. An existing managed session keeps the configuration captured when it was created; editor changes apply to the next workflow/MCP process.

## Pull-request review contract

Git MCP supplies exact patches. `copilot_review_start` and `copilot_review` accept optional `reviewInstructions` (maximum 32,000 characters) and `existingCommentsJson`. Existing comments contain path, optional side/line range, body, and optional fingerprint; only comments relevant to the current batch are included as bounded untrusted model context. Copilot review results contain fingerprint, severity, category, confidence, path, diff side, line range, evidence, explanation, and optional suggested patch. The server rejects unknown paths and lines outside the supplied diff, removes matching fingerprints or equivalent location/body findings, and reports binary/submodule skips plus truncated files.

The review session has no tools, no configuration discovery, no write permission, and is deleted after completion. Publication is deliberately outside this MCP and belongs to the Flow agent plus the official GitHub MCP. The publication gate fails closed for stale SHAs, dry runs, no findings, and unapproved interactive runs. `auto_comment` can only submit `COMMENT`; automated approval and merge are unsupported.

`code_suggest_change` and `code_agent_edit` also accept an optional `provider` parameter. When omitted, the default GitHub Copilot SDK behavior above is unchanged.
When provided, the MCP reads the matching provider from its typed `CodeCopilotSettings.Providers` dictionary and passes it as a custom Copilot SDK provider for that call. At process startup, the MCP uses the storage-agnostic `IKeyVaultSecretCatalogReader` contract to map shared provider secrets and MCP-specific leaf overrides into the `Code:Copilot` configuration namespace before typed settings are created. Database resolution, SQL, decryption, auditing, and storage-specific failures remain encapsulated by `GnOuGo.KeyVault.Core`.
Supported provider section names are:

- `Code:Copilot:Providers:<provider>`
- compatibility fallback: `Code:Copilot:Providers:LLM--Models--<provider>`
- legacy fallback: `Code:Copilot:Providers:gnougo_llm_<provider>`

The section must contain at least `url`; `model` is recommended and falls back to `Code:Copilot:Model` when omitted. Supported provider fields include `type`, `wireApi`, `wireModel`, `authType`, `apiKey`, `bearerToken`, and OIDC fields such as `oidcIssuer`, `oidcClientId`, `oidcScopes`, `oidcClientSecret`, or `oidcPrivateKeyPem`. Keep secret values in KeyVault, environment variables, or another secure configuration provider; do not commit real tokens to `appsettings.json`.

For local Agent/Desktop usage, LLM provider secrets saved by `/llm add` are stored through `GnOuGo.KeyVault.Core` with keys such as `LLM--Models--OpenAi` and legacy `gnougo_llm_OpenAi`. Provider identity is case-insensitive. One canonical key wins over one legacy key; multiple same-priority canonical or legacy variants are ambiguous and fail startup without exposing their values. Agent configuration saves reuse the existing canonical key and retire equivalent aliases so the ambiguity cannot be recreated by a case-only edit. Provider JSON is flattened into `Code:Copilot:Providers:<provider>:...`, and secrets prefixed with `LLM--McpServerOverrides--GnOuGo.GithubCopilot.Mcp--Code--Copilot--` are mapped to the matching `Code:Copilot:...` leaves. Objects and indexed collections are supported.

Configuration precedence is packaged/appsettings/environment/command line, then shared provider secrets, then MCP-specific KeyVault overrides. The complete KeyVault overlay therefore has the highest priority, with MCP-specific values winning over shared provider fields. It is loaded once when the MCP process starts. If optional KeyVault storage is missing or unavailable, the entire overlay is discarded and a redacted warning is logged; if a present value is malformed, ambiguous, or invalid for a typed setting, startup fails without logging the value.

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
dotnet build .\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj
.\src\GnOuGo.GithubCopilot.Mcp\bin\Debug\net10.0\GnOuGo.GithubCopilot.Mcp.exe
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

The project is configured for Native AOT and trimming analysis. Source-level `IL2026`, `IL3050`, and `IL3055` diagnostics are treated as build errors. The tool consumes the EF Core-backed KeyVault boundary, so normal publishes suppress only the pinned EF package summaries `IL2104` and `IL3053`; `verify-warning-free-publishes.ps1 -AuditKnownTrimWarnings` re-enables them and verifies their exact origins.

```powershell
dotnet publish "C:\github\GnouGo\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:InvariantGlobalization=false -p:SkipModelMetadataGeneration=true
```

CI validates a dedicated `win-x64` Native AOT publish for `GnOuGo.GithubCopilot.Mcp` in `.github/workflows/build-agent-desktop-trimmed.yml`.
