# GnOuGo.GithubCopilot.Core

Publishable .NET 10 library containing the GitHub Copilot SDK integration used by GnOuGo. It is independent of MCP transport and can be tested with fake SDK clients.

## Stable surface

The library pins `GitHub.Copilot.SDK` `1.0.8` and maintains an explicit GA allowlist. Experimental, preview, insiders, fleet, fork, remote/cloud sandbox, canvas, extensions, manual compaction, history truncation, agent-management, citations, and unknown RPC APIs are rejected.

It provides:

- tenant-bound opaque managed-session handles with create, resume, list, foreground, disconnect, delete, serialized sends, abort, history, TTL cleanup, model/mode switching, plans, attachments, workspace files, skills, tool filtering, permissions, user input, elicitation, and MCP configuration contracts;
- ephemeral interactive one-shot execution through a managed session that is permanently deleted in a cancellation-independent `finally` path;
- one-shot sessions that always disconnect and delete persisted SDK state;
- `interactive`, `auto_approve_allowlist`, `deny`, and policy-gated `approve_all` permission modes. Interactive callbacks always offer allow-once and refuse. When the SDK supplies a safe matching scope they also offer allow-similar-for-task. A disabled-by-default host gate controls broad current-task, workflow-run, and future-agent-run choices without changing the session to `approve_all`;
- KeyVault-provider abstractions that keep credentials out of workflow arguments and results;
- bounded pull-request review batches, caller instructions applied to every batch, bounded untrusted existing-comment context, strict structured finding parsing, diff-line/path validation, fingerprints, existing-comment deduplication, and coverage metadata;
- a fail-closed publication gate for `dry_run`, `interactive`, and `auto_comment` policies. It never represents GitHub `APPROVE` or merge operations;
- source-generated JSON metadata for trimming and Native AOT consumers.

Raw model reasoning is discarded. Streaming exposes only operational progress events.
Interactive one-shot execution reports stable lifecycle milestones for session creation, request processing, cancellation/failure, and session deletion. A deletion failure never replaces an earlier request failure; it is attached as cleanup diagnostics while the primary exception is preserved.
Narrow task permissions are held by the native SDK session and disappear when the managed session is deleted or expires. Broad current-task grants remain local to that ephemeral task. Workflow-run and future-agent-run grants are accessed only through `ICopilotPermissionGrantStore`; Core does not depend on a persistence implementation. Sandbox-bypass requests are excluded from every reusable grant and always require a separate allow-once decision. Every requested, granted, automatically reused, or refused operation is emitted through `ICopilotPermissionEventSink` with safely redacted details and execution correlation.

## Build and test

```bash
dotnet build src/GnOuGo.GithubCopilot.Core/GnOuGo.GithubCopilot.Core.csproj
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests/GnOuGo.GithubCopilot.Core.Tests.csproj
dotnet pack src/GnOuGo.GithubCopilot.Core/GnOuGo.GithubCopilot.Core.csproj -c Release
```
