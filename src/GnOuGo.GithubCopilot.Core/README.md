# GnOuGo.GithubCopilot.Core

Publishable .NET 10 library containing the GitHub Copilot SDK integration used by GnOuGo. It is independent of MCP transport and can be tested with fake SDK clients.

## Stable surface

The library pins `GitHub.Copilot.SDK` `1.0.8` and maintains an explicit GA allowlist. Experimental, preview, insiders, fleet, fork, remote/cloud sandbox, canvas, extensions, manual compaction, history truncation, agent-management, citations, and unknown RPC APIs are rejected.

It provides:

- tenant-bound opaque managed-session handles with create, resume, list, foreground, disconnect, delete, serialized sends, abort, history, TTL cleanup, model/mode switching, plans, attachments, workspace files, skills, tool filtering, permissions, user input, elicitation, and MCP configuration contracts;
- one-shot sessions that always disconnect and delete persisted SDK state;
- `interactive`, `auto_approve_allowlist`, `deny`, and policy-gated `approve_all` permission modes. Interactive callbacks offer allow-once, refuse, and—only when the SDK supplies a safe matching scope—allow-similar-for-session. Remembered scopes are command identifiers, exact MCP server/tool pairs, URL domains, exact custom tools, or supported filesystem read/write categories; unrestricted, permanent, and location-level grants are never offered;
- KeyVault-provider abstractions that keep credentials out of workflow arguments and results;
- bounded pull-request review batches, caller instructions applied to every batch, bounded untrusted existing-comment context, strict structured finding parsing, diff-line/path validation, fingerprints, existing-comment deduplication, and coverage metadata;
- a fail-closed publication gate for `dry_run`, `interactive`, and `auto_comment` policies. It never represents GitHub `APPROVE` or merge operations;
- source-generated JSON metadata for trimming and Native AOT consumers.

Raw model reasoning is discarded. Streaming exposes only operational progress events.
Session-scoped permissions are held by the native SDK session and disappear when the managed session is deleted or expires.

## Build and test

```bash
dotnet build src/GnOuGo.GithubCopilot.Core/GnOuGo.GithubCopilot.Core.csproj
dotnet test tests/GnOuGo.GithubCopilot.Core.Tests/GnOuGo.GithubCopilot.Core.Tests.csproj
dotnet pack src/GnOuGo.GithubCopilot.Core/GnOuGo.GithubCopilot.Core.csproj -c Release
```
