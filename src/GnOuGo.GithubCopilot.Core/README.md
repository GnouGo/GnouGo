# GnOuGo.GithubCopilot.Core

Publishable .NET 10 library containing the GitHub Copilot SDK integration used by GnOuGo. It is independent of MCP transport and can be tested with fake SDK clients.

Hosts can inject the SDK's `CopilotRequestHandler` into `GitHubCopilotSdkClientFactory`.
`CopilotInferenceProxyHandler` routes inference through an explicit loopback HTTP
policy host, preserving the original destination and request. It provides no direct
fallback or WebSocket bypass. The policy host must enforce budgets and validate
the configured upstream; credentials remain in request memory.

## Stable surface

`CopilotSendResult.Completed` reports completion of the assistant turn. It does not certify
successful execution of the requested work. `Content` is assistant response text; callers
must establish business outcomes from verified execution observations. These distinctions
are included in the exported MCP result schemas.
`ToolExecutions` retains SDK tool-call identities, exact arguments and terminal process
exit codes separately from assistant text and tool invocation success. Incomplete,
missing or conflicting observations cannot certify successful work. This additive
result field defaults to an empty list when reading older serialized results. Both the
legacy `terminal` and current `shell_exit` SDK receipts are supported; output-preview
truncation and output-file metadata remain explicit.

The library pins `GitHub.Copilot.SDK` `1.0.14`, bundles Copilot CLI `1.0.88`, and maintains an explicit GA allowlist. The CLI version supplies the native runtime wrapper and its adjacent runtime library required by this SDK. Experimental, preview, insiders, fleet, fork, remote/cloud sandbox, canvas, extensions, manual compaction, history truncation, agent-management, citations, and unknown RPC APIs are rejected.

It provides:

- tenant-bound opaque managed-session handles with create, resume, list, foreground, disconnect, delete, serialized sends, abort, history, TTL cleanup, model/mode switching, plans, attachments, workspace files, skills, tool filtering, permissions, user input, elicitation, and MCP configuration contracts;
- ephemeral interactive one-shot execution through a managed session that is permanently deleted in a cancellation-independent `finally` path;
- one-shot sessions that always disconnect and delete persisted SDK state;
- `interactive`, `auto_approve_allowlist`, `deny`, and policy-gated `approve_all` permission modes. Interactive callbacks always offer allow-once and refuse. When the SDK supplies a safe matching scope they also offer allow-similar-for-task. A disabled-by-default host gate controls broad current-task, workflow-run, and future-agent-run choices without changing the session to `approve_all`; a second independent gate controls explicit reusable sandbox-bypass choices;
- KeyVault-provider abstractions that keep credentials out of workflow arguments and results;
- bounded pull-request review batches, caller instructions applied to every batch, bounded untrusted existing-comment context, strict structured finding parsing, diff-line/path validation, fingerprints, existing-comment deduplication, and coverage metadata;
- source-generated JSON metadata for trimming and Native AOT consumers.

Review analysis returns findings and coverage; it does not derive a publication verdict or authorize writes. GitHub mutations use the configured official GitHub MCP through the consuming workflow. The former evaluation/publication API has been removed; see [migration details](../../docs/github-mcp-workflow-execution.md).

Raw model reasoning is discarded. Streaming exposes only operational progress events.
Interactive one-shot execution reports stable lifecycle milestones for session creation, request processing, cancellation/failure, and session deletion. A deletion failure never replaces an earlier request failure; it is attached as cleanup diagnostics while the primary exception is preserved.
Narrow task permissions are held by the native SDK session and disappear when the managed session is deleted or expires. Broad current-task grants remain local to that ephemeral task. Workflow-run and future-agent-run grants are accessed through `ICopilotPermissionGrantStore`; stores that support explicit bypass grants additionally implement `ICopilotSandboxBypassPermissionGrantStore`, so Core does not depend on a persistence implementation. Ordinary broad grants exclude sandbox bypass. When both host gates are enabled, a user can explicitly grant sandbox bypass for the current task, workflow run, or future runs of the same agent; the persistent scope requires a second confirmation. Every requested, granted, automatically reused, or refused operation is emitted through `ICopilotPermissionEventSink` with safely redacted details and execution correlation.

## Controlled filesystems

Hosts inject `ICopilotSessionFileSystemFactory` into `CopilotSessionManager` and enable
`UseSessionFileSystem` in the runtime configuration. Core owns the SDK registration,
per-session lifetime, permission checks, and create/resume callbacks. The host owns
project root, file type, size, and write policy. Permission grants cannot override that
policy. SDK session-state files use a separate in-memory filesystem, retained during
disconnect/resume and cleared on deletion or expiry; they are never written into the
project. Handles remain process-local, as before. Final cleanup disconnects the SDK and
clears the owned transient store; SDK disk deletion is used only for native storage.

Controlled sessions expose Core's `project_read`, `project_write`, `project_append`,
`project_list`, `project_stat`, `project_mkdir`, `project_remove`, and `project_rename`
functions. Native file tools and child-agent tools are excluded so file I/O cannot bypass
the injected policy. Tool allowlists are still applied; hosts using an explicit allowlist
must name the required `project_*` functions. Deny-mode suggestions receive no file tools.
The pre-tool hook and permission callback enforce host policy before remembered approval,
and each filesystem operation validates again before accessing the project.

`ICopilotHumanInputProvider.Capture()` lets a transport snapshot the current invocation.
Core binds that snapshot to each active session operation so SDK background callbacks
reach the current send request after reconnect, without inheriting another session's
transport context. The default implementation remains compatible with context-free hosts.

File routing is not an OS sandbox for arbitrary commands. Commands retain the native
CLI sandbox settings and Core permission/HITL boundary. Operational progress can be
observed through `CopilotSendRequest.Progress`; reasoning content is excluded. Usage
metadata and modified-file snapshots are additive result fields.

## Build and test

```bash
dotnet build src/GnOuGo.GithubCopilot.Core/GnOuGo.GithubCopilot.Core.csproj
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests/GnOuGo.GithubCopilot.Core.Tests.csproj
dotnet pack src/GnOuGo.GithubCopilot.Core/GnOuGo.GithubCopilot.Core.csproj -c Release
```

`CopilotReviewStartRequest.RuntimeContextJson` optionally carries a JSON object of upstream execution results (maximum 32,000 characters). The review manager validates it before creating a session and preserves it as encoded untrusted context in every batch. Caller instructions and existing inline comments remain separate inputs; runtime context grants no permission to act.
