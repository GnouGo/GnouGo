# GnOuGo.GithubCopilot.Mcp

Interactive permission and business-question callbacks are serialized within each MCP invocation, including captured asynchronous callbacks. Each question still requires its own answer and retains cancellation, tenant context and permission checks. MCP SDK 2.2.0 has a separate ten-round input-required limit; sufficiently long interactive calls can still stop at that limit. See the [live validation evidence](../../docs/planning-schema-portability-2026-10-02.md).

MCP stdio server for safe code operations on a local project.

Optional `Code:Copilot:InferenceProxyEndpoint` routes both managed Copilot sessions
and legacy code operations through a loopback inference policy host. The host must
accept `POST <endpoint>/ready` with `sdk-http-interception-v1`, then enforce and
forward inference requests using `X-GnOuGo-Inference-Upstream` and
`X-GnOuGo-Inference-Request`. Startup fails when this explicit policy host is
unavailable. No direct HTTP or WebSocket fallback is allowed. KeyVault remains the
highest-priority configuration source; the proxy setting contains no credentials.

## MCP protocol compatibility

This stdio server uses the stable C# MCP SDK `2.0.0` with automatic protocol negotiation: clients prefer `2026-07-28` discovery and can initialize with stable `2025-11-25`. Launch the built apphost, or use `dotnet GnOuGo.GithubCopilot.Mcp.dll`; do not put `dotnet run` on an MCP stdio transport because CLI output can corrupt the JSONL stream. The GnOuGo progress stream remains a stderr side channel and does not alter the MCP wire contract.

## Features

For message and one-shot results, `completed` means the assistant turn completed. It does
not establish that the requested work succeeded. The advertised field descriptions keep
turn completion separate from verified execution outcomes and assistant response text.
`toolExecutions` exposes tool-start arguments and terminal results captured directly
from SDK events. Each terminal observation preserves the process exit code, working
directory and output separately from the tool invocation's success flag. Verify every
required command against these observations. Empty observations, absent exit codes and
conflicting completion events remain inconclusive; assistant prose never fills them in.
The same invocation already returns these observations, so consuming them requires no
additional external read operation. Legacy results deserialize with an empty list.

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
- Use review results as workflow data. GitHub mutations use the configured official GitHub MCP through normal workflow execution and its generic approval/confirmation mechanisms.

Git repository workflows are provided by the separate `GnOuGo.Git.Mcp` tool.

## Typed attachments (breaking contract)

`copilot_session_send`, `copilot_one_shot` and `copilot_interactive_one_shot` accept
`attachments`, an optional array of closed objects. `attachmentsJson` has been removed
and is explicitly rejected. Omission, null and `[]` mean no attachments.

```json
{"attachments":[{"type":"file","path":"docs/design.md"},{"type":"blob","content":"aGVsbG8=","path":"note.txt","mimeType":"text/plain"}]}
```

File paths must be nonblank; files must exist inside the approved project and remain
subject to read permissions and sandbox policy. Blob content must be base64. Blob
`path` is an optional display name; omitted/null `mimeType` retains
`application/octet-stream`. Unknown kinds, extra fields, null items, encoded JSON
strings and arbitrary context objects are invalid. Syntax validation runs before
session creation/sending and returns sanitized `INVALID_INPUT` locations. Discovery
publishes the same types and constraints; it does not grant filesystem access.

Put business context (such as a review URL and instructions) in `prompt`, never in
attachments. Deploy the updated MCP, refresh discovery, then explicitly revise or
regenerate and approve affected workflows. Saved executions and approvals are not
rewritten or restarted. Attachment validation does not prove that a task will complete.

Natural-language allowlist entries such as “install dependencies” or “run tests” do
not grant command execution. Use the declared permission mechanism for the requested
work; do not substitute `approve_all`. Sandbox restrictions, including dependency
download restrictions, remain unchanged.

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

Interactive permission, user-input, and nested MCP elicitation callbacks are bridged through stable MCP form elicitation. `copilot_session_create` publishes the managed-session permission enum and defaults to `interactive`. `copilot_one_shot` is deliberately non-interactive, publishes only `auto_approve_allowlist`, `deny`, and `approve_all`, and defaults to `deny`; its native `permissionAllowlist` array supplies the explicit allowlist when that mode is selected. Use `copilot_interactive_one_shot` for dependency installation, tests, linting, edits, or other one-turn work that may execute tools: it creates a managed interactive session and permanently deletes it after success, failure, or cancellation. `deny` is appropriate for pure review inference. `auto_approve_allowlist` permits only explicitly named read-only paths/tools. `approve_all` is rejected unless the host gate is enabled and must not be generated without explicit unattended intent and established host availability.
Interactive permission prompts show the exact operation, warnings, sandbox-bypass status, and remembered scope. They offer **Allow once**, **Refuse**, and **Allow similar operations for this task** only when the SDK marks a matching scope as safe. When `EnableApproveAll` is enabled, the same interactive callback may also offer **Allow all for this Copilot task**, **Allow all for this workflow run**, and **Allow all future runs for this agent** when the required stable identities are available. Ordinary broad grants never include sandbox bypass. When `EnableSandboxBypassGrants` is also enabled, a bypass request offers explicit task, workflow, and future-agent choices that include ordinary and bypass operations. Future-agent approval requires a second confirmation and is stored by tenant plus stable agent ID, so it follows renames and survives restarts. Workflow grants are tenant/run scoped and expire after inactivity.
Automatically reused permissions do not create an elicitation card. They emit redacted `permission.requested` and `permission.auto_approved` progress entries, including the sandbox-bypass flag, while explicit answers emit `permission.granted` or `permission.refused`. Grant revocation emits `permission.grant.revoked`. Raw model reasoning and likely credentials are never included.
Every interactive elicitation carries the originating `_meta.gnougo` correlation back to the Flow client. The active tool cancellation token is linked to the Copilot callback, so cancelling the workflow releases the pending permission request instead of leaving the stdio server waiting. Interactive one-shot progress includes session creation, request processing, permission requested/resolved, completion or cancellation, and session deletion; it never includes raw reasoning.

Future-agent permission grants are encrypted and stored through the storage-agnostic `IKeyVaultRecordStore` contract in `GnOuGo.KeyVault.Core`, using the tenant-isolated `github-copilot.permission-grants` collection. The MCP contains no SQL or storage-path logic. Workflow-run grants remain in memory and expire after inactivity. Management-only tools create (with explicit human confirmation), list and revoke future-agent grants; their `gnougo.management.visibility=management_only` metadata excludes them from ordinary workflow generation. Agent Server exposes them as `/mcp copilot permissions` and `/mcp copilot permissions revoke <grant-id>`. Deleting an agent revokes its persistent grants. Existing grants in the legacy `gnougo-copilot-permissions.db` file are not migrated or deleted; they require explicit approval again.

When hosted by Agent Server/Desktop, `/mcp edit GnOuGo.GithubCopilot.Mcp` edits provider, fallback model, reasoning effort, logged-in-user authentication, request timeout, managed-session TTL, broad approvals, and reusable sandbox-bypass approvals. Every override is encrypted separately under `LLM--McpServerOverrides--GnOuGo.GithubCopilot.Mcp--...`. Selecting a field under **inherit fields** deletes only that override. Enabling reusable sandbox bypass automatically enables broad approvals. Command, arguments, roots, extensions, write policy, and credentials remain protected. Provider credentials remain in `LLM--Models--<provider>` and are never copied into MCP-specific entries. Agent Server passes only `KeyVault__DatabasePath` to this direct-reader MCP; it never injects decrypted `Code__Copilot__...` values.

The editor's **agent_permissions → Configure agent permissions** action lists existing agents with their stable IDs, displays the current tenant-owned grant, and offers **Keep current**, **Allow All including sandbox bypass**, or **Remove persistent approval**. Allow All explicitly persists both host gates and opens a fresh MCP process using the refreshed KeyVault configuration. The management-only `copilot_permission_grant_create` tool then asks **Confirm persistent sandbox-bypass approval**; cancelling stores no grant. Confirmation uses MCP elicitation directly, never the reusable auto-approval callback. The default broad-approval gate displayed by the editor is `false`, matching the MCP.

A confirmed grant automatically approves permission callbacks from the selected agent's first subsequent request. It follows renames and survives restart; other agents receive no grant. Business questions remain interactive. Explicit denial modes, host filesystem confinement, approved capabilities and bounded-run restrictions still apply; bounded runs prohibit sandbox expansion even with a persistent grant. See [diagnosis and validation](../../docs/agent-permissions-and-animation.md).

Provider resolution order is an explicit tool argument, the MCP provider override, then the Agent default. Model resolution order is the selected provider's KeyVault model, the MCP fallback model, then the packaged fallback. An existing managed session keeps the configuration captured when it was created; editor changes apply to the next workflow/MCP process.

## Pull-request review contract

Git MCP supplies exact patches. `copilot_review_start` and `copilot_review` accept optional `reviewInstructions` (maximum 32,000 characters) and `existingCommentsJson`. Optional `runtimeContextJson` is a JSON object (at most 32,000 characters) containing original upstream execution results under named keys, including failures and uncertainty. It is validated before session creation and included as encoded, untrusted context in every batch; it cannot authorize publication or replace caller instructions or existing comments. Existing comments contain path, optional side/line range, body, and optional fingerprint; only comments relevant to the current batch are included as bounded untrusted model context. Copilot review results contain fingerprint, severity, category, confidence, path, diff side, line range, evidence, explanation, and optional suggested patch. The server rejects unknown paths and lines outside the supplied diff, removes matching fingerprints or equivalent location/body findings, and reports binary/submodule skips plus truncated files.

The review session has no tools, no configuration discovery, no write permission, and is deleted after completion. Results include `complete`, derived from completed batches, coverage and rejected invalid findings, plus `blockingFindingCount`, which retains blockers whose duplicate comments were suppressed. An empty model response is invalid; an explicit `[]` can represent a completed review with no findings.

Review analysis does not publish or authorize a GitHub mutation. Workflows use the configured official GitHub MCP for those operations, with normal workflow approval, generic runtime confirmation and MCP elicitation. No host-managed draft, publication gate, fresh-head check or publication replay guarantee is supplied. See [MCP workflow execution and removed publication APIs](../../docs/github-mcp-workflow-execution.md).

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

`code_agent_edit` is a compatibility wrapper over Core's ephemeral managed interactive
session. `code_suggest_change` also uses Core, with tool execution disabled. MCP never
creates SDK clients or sessions. The shared configuration builder preserves provider,
telemetry, trace, and timeout settings for both legacy and managed tools.

All agentic sessions use the host's controlled project filesystem through Core. It
rejects traversal, symbolic links, protected directories, disallowed file types and
oversized content, and applies `Code:AllowWrites` to every mutation. Recursive moves
and deletions validate all descendants first. Internal SDK state is isolated in memory.
Core registers `project_*` file functions instead of native file/child-agent tools. Explicit
SDK tool allowlists must include the needed `project_*` functions. The same provider and
permission callbacks are installed on resume; active MCP context is captured per call.
Commands retain existing CLI sandbox and Core HITL settings; filesystem routing alone
does not contain shell commands.

Tenant-scoped calls require transport metadata `_meta.gnougo.tenantId`. Business tools no longer accept `tenantId`; there is no environment, activity or default-tenant fallback for ownership. Bounded task envelopes must match the transport tenant. Standalone callers must send metadata; Agent.Server supplies the execution owner. Refresh discovery and revise/regenerate workflows using the removed argument; do not turn tenant identity into a workflow input. Agentic execution defaults to interactive permission requests and fails
closed when human input is unavailable. Broad approval is disabled in shipped defaults.
`code_agent_edit` additionally exposes Core's `toolExecutions` observations.
This lets Copilot edit files directly through the MCP process while still enforcing the same project policy as manual file writes:

- `Code:AllowWrites` must be `true`.
- Paths must stay inside the resolved project root / allowed roots.
- File extensions must be allowlisted by `Code:AllowedExtensions`.
- Parent traversal and wildcard paths are rejected.

The older `code_suggest_change` tool remains suggestion-only and does not write files.

Both `code_suggest_change` and `code_agent_edit` emit progress milestones as structured JSONL stderr messages while the call is running, and include the same events in the final `progressEvents` array.

`progressEvents` is the official GnOuGo contract. Application milestones and native `GitHub.Copilot.SDK` session events are both normalized to this schema before they leave this MCP server. `GnOuGo.Flow.Core`, Agent Server, and the UI must consume this contract instead of coupling directly to SDK-specific event classes or payload shapes. When the SDK exposes useful complete events, Core maps them to stable operational `kind` values; when it does not, the explicit GnOuGo milestones still provide progress.

Each item contains:

- `kind`: stable machine-readable phase, for example `prepare`, `provider`, `session_create`, `request_send`, `completed`, `file_modified`, or SDK-mapped phases such as `assistant.turn_start` and `tool.execution_progress`.
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

## Native list parameters (breaking migration)

List inputs are native JSON arrays, visible to discovery and validated before tool
binding, filesystem reads or session creation:

| Tools | Optional list parameters |
| --- | --- |
| `copilot_session_create` | `permissionAllowlist`, `availableTools`, `excludedTools`, `skillDirectories`, `disabledSkills` |
| `copilot_one_shot`, `copilot_interactive_one_shot` | `permissionAllowlist` |
| `code_suggest_change`, `code_agent_edit` | `contextFiles` |

For example, use `"contextFiles": ["src/App.cs"]`, with no JSON encoding around the
array. Entries must be nonblank strings. Objects, encoded JSON strings, null items
and blank entries produce a structured `INVALID_INPUT` error with the parameter
and index, without echoing the supplied value. Omission and explicit null retain
their previous defaults; `[]` remains an explicitly empty Copilot configuration
list. Context-file omission/null/empty arrays all mean no supplied file context.
Deduplication preserves the first occurrence, with ordinal comparison for Copilot
configuration and ordinal case-insensitive comparison for context files.

The corresponding six `*Json` argument names are removed and explicitly rejected,
even when null or accompanied by the new argument. Refresh MCP discovery, revise
the affected TaskPlan and approve the newly compiled artifact. Historical stored
workflows and approvals are not rewritten. This does not change object-based JSON
parameters such as review files/comments.

An allowlist still permits only read-only operations: `["npm"]` does not authorize
`npm install`. Installation/testing/editing uses `copilot_interactive_one_shot` with
the existing tenant/agent grants or human approval. No command/scope object is
flattened into broader permissions. See [diagnosis and execution evidence](../../docs/copilot-list-contracts.md).

## Native AOT publish

macOS Release AOT publishes normalize a local copy of the .NET 10 Apple cryptography
archive's incomplete debug information. Code and link symbols are retained; the
NuGet cache is unchanged. See the [published certificate-chain smoke and framework
workaround](../../tests/GnOuGo.Flow.Planning.Smoke/README.md).

The project is configured for Native AOT and trimming analysis. Source-level `IL2026`, `IL3050`, and `IL3055` diagnostics are treated as build errors. The tool consumes the EF Core-backed KeyVault boundary, so normal publishes suppress only the pinned EF package summaries `IL2104` and `IL3053`; `verify-warning-free-publishes.ps1 -AuditKnownTrimWarnings` re-enables them and verifies their exact origins.

```powershell
dotnet publish "C:\github\GnouGo\src\GnOuGo.GithubCopilot.Mcp\GnOuGo.GithubCopilot.Mcp.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:InvariantGlobalization=false -p:SkipModelMetadataGeneration=true
```

CI validates a dedicated `win-x64` Native AOT publish for `GnOuGo.GithubCopilot.Mcp` in `.github/workflows/build-agent-desktop-trimmed.yml`.

## Local editing acceptance

The opt-in [controlled editing fixture](../../tests/GnOuGo.GithubCopilot.E2E.Tests/README.md#controlled-local-editing)
runs both legacy and managed MCP entry points with a real model, bounded allow-once
elicitation, failing/passing Python tests, SDK command receipts, reconnect, and refusal.
It supports the published Native AOT binary and performs no remote publication.

## Bounded Flow tasks (schema 9)

`GnOuGo.Flow.Copilot` uses `copilot_task_contract`, `copilot_task_validate`,
`copilot_task_run` and `copilot_task_inspect`. These adapter operations are hidden
from general workflow capability discovery; planners select the registered
`agent.run` contract instead. Each task uses the existing managed session lifecycle.

Task scope, intent, cumulative inference reservations and final receipts are stored
through encrypted KeyVault record APIs. An invocation is never dispatched twice.
After a process crash, inspection returns a saved receipt or `needs_reconciliation`;
it does not promise restoration of the SDK session. Cross-process ownership uses
workspace-resolved owner files, so all hosts sharing the record store must share
the same workspace owner directory.

`copilot_task_contract` reads validated device policy through the existing SDK without
creating a session or sending a prompt. Its schema omits `command.execute` and
`command.exit` when mandatory policy is absent, invalid or unreadable, or host writes
are disabled. File capabilities retain their existing policy. Discovery snapshots
are versioned; refresh discovery after an administrator changes policy. Configured
policy is not proof of working session enforcement: task validation rechecks policy
and session preparation still probes actual enforcement before inference.

Project tools retain the existing filesystem and permission policies. Adaptive
commands require a mandatory, available Copilot sandbox: managed `sandbox.enabled`
and `sandbox.failIfUnavailable` must both be enabled. The task disables sandbox
bypass, outbound/local networking and Git/GitHub credential grants. It retains
host-defined sandbox read-only access and allows writes in the task workspace.
The adapter reads device policy through the SDK, enables managed policy and injects
a restrictive bypass-permissions setting on creation and resume. Authentication
stays on the existing client configuration, preserving BYOK providers; per-session
GitHub tokens conflict with BYOK in runtime 1.0.88. No credentials are passed to
task commands. Missing policy, invalid policy and unavailable enforcement fail
before the task prompt is dispatched. See the [upstream managed
sandbox policy](https://docs.github.com/en/copilot/reference/enterprise-administrators/enterprise-managed-settings).

Inference retains the configured policy proxy. Supported text HTTP protocols have
explicit output ceilings and conservative, non-refundable token reservations;
opaque prior-conversation references, multimodal requests and unaccounted WebSockets
are rejected. Receipts label reservations `reserved_upper_bound`.

Terminal preparation failures carry an optional safe `failure` alongside the existing
result/receipt fields. Codes distinguish `AGENT_ISOLATION_REQUIRED`,
`AGENT_ISOLATION_POLICY_INVALID`, `AGENT_ISOLATION_UNAVAILABLE` and
`AGENT_PREPARATION_FAILED`. These messages come from known host conditions, never
assistant output or arbitrary exception bodies. Unknown external outcomes still
require reconciliation. Update the MCP and Flow runtime together; old receipts remain
readable, but older strict readers may reject the additive failure field.

Verification evidence comes from SDK tool events and controlled file reads. Exact
command subjects include the final exit code and retained attempt history; earlier
failed tests are not erased. A test before a later file edit or non-verification
command cannot establish final success. File evidence includes an SHA-256 digest of
the observed UTF-8 content and whether it changed during this invocation. Required
verification commands must themselves be suitable checks of the task outcome.

```sh
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests
dotnet test tests/GnOuGo.GithubCopilot.Mcp.Tests
dotnet test tests/GnOuGo.Flow.Copilot.Tests
```

Agent budget stops retain safe admission diagnostics and execution observations. Only verified cessation permits a terminal failure and cleanup; unknown outcomes require explicit reconciliation. See [budget stops and recovery](../../docs/agent-budget-interruptions.md).
