# Protocol selection and trace inspection — validation

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

Date: 2026-09-21. Branch: `feat/deterministic-planner-v2`.
Starting revision: `6655f5a`.
Configuration change: `d476717`. Tracing change: `d00c528`.
No paid model evaluation, real PR execution or GitHub publication occurred.

## Delivered behavior

The existing provider wizard saves an explicit Responses/background or Chat
Completions/foreground selection, applies it live and uses it for its existing
validation request. Missing saved values preserve base configuration; invalid
values are rejected. Configuration edits preserve credentials, API version and
unrelated policies. There is no automatic fallback.

Server captures logical LLM calls through one shared implementation used by its
snapshot and dynamic runtime adapters. Flow supplies an explicit runtime stage;
HTTP attempts are events under the logical call. The Pipeline view separates
calls, transport attempts, workflow stages and durable journal history. It shows
known versus unknown usage, estimates, errors, repair target counts and retained
request/schema/response/tool-call details.

New diagnostic content uses encrypted tenant-scoped KeyVault records, seven-day
retention and a 2 MiB document limit. Planner reservations and receipts are reused,
not copied or rewritten. Private raw provider responses are excluded. Server step
content logging is disabled; the existing LLM response preview now respects that
setting. The implementation does not change planner behavior, budgets, retry
rules, approval, public planning APIs or schema-7 storage.

## Automated checks

All tests below used Release configuration, serialized .NET builds (`-m:1`) and
warnings as errors. No source warnings were suppressed.

| Suite | Passed |
| --- | ---: |
| AI.Core | 222 |
| Flow | 853 |
| Flow.Planning | 199 |
| Flow.Integrations | 76 |
| Agent.Server | 368 |
| Observability.Core | 3 |
| OtlpCollector.Server | 7 |
| **Total** | **1,728** |

Command pattern:

```sh
dotnet test tests/<component>.Tests -c Release -m:1 --no-restore -warnaserror
```

Additional checks passed:

- Warning-free Release Server and Desktop builds.
- `pnpm install --frozen-lockfile` and `pnpm build` in Server/ClientApp. The host
  did not expose `corepack`; the installed pnpm 10.14.0 was used instead.
- Self-contained, trimmed macOS ARM64 Server publish, with no trim warnings:
  `dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -p:PublishTrimmed=true -p:PublishAot=false -p:SkipBundledServerTools=true -p:UseAppHost=true -o <temporary-export> -warnaserror`.
- Native AOT planner publish and all eight offline smoke cases (one interpretation
  call, zero repairs each); no new benchmark campaign was created.
- Published Server `--planning-persistence-smoke <temporary-directory>`: schema-7
  persistence, encrypted review drafts, tenant isolation and uncertain-publication
  replay passed.

Tests cover actual endpoint selection with fake HTTP handlers, invalid protocol
values, live updates and setting preservation; encrypted content and tenant/trace
ownership; missing usage, cancellation, transport uncertainty, capture failure,
parallel stage isolation, document limits and retention; journal reuse and
read-only historical lookup; safe lazy expansion, mismatched trace rejection and
logical-call deduplication, including provider child spans. Existing receipt,
restart, accounting, approval and security suites remained green.

## Chromium and retained evidence

An isolated published Server used dedicated test databases and a loopback fake
HTTP provider. Native production credentials were not used for configuration
checks. Both `/llm add` and `/llm edit openai` were operated through normal chat
cards, including the existing Save confirmation.

Observed results:

- New configuration selected Background — Responses API by default.
- The validation request reached `/v1/responses` with `background=true`.
- Editing preselected the saved protocol; selecting Chat Completions reached
  `/v1/chat/completions` without a background request field.
- Chat Completions remained preselected after restarting the Server.
- With `TraceDebug.Enabled=true`, expansion displayed the encrypted request and
  response, byte breakdown, reported test usage and cost estimate. Raw/formatted
  controls, search and explicit copy worked. The sample validation prompt was
  30 UTF-8 bytes, labelled as an eight-token estimate; the fake provider reported
  12 input and six output tokens separately.
- Browser testing found a Razor string-parameter binding error. It was corrected
  before delivery and covered by a regression that expands owned encrypted
  content and rejects another trace's identifiers.
- The repository's `TraceDebug.Enabled=false` default was respected. The viewer
  explains how to enable detailed capture. Old calls made with capture disabled
  do not acquire invented payloads.

A separate isolated host inspected existing encrypted planning sessions through
Workflow designer, with background planning disabled. No planning command or
inference was dispatched:

| Retained session | Journal entries | Inspection result |
| --- | ---: | --- |
| `e62218e7…` | 1 | Original request/schema visible; missing receipt and unknown usage explicit |
| `f405f9a7…` | 7 | Original requests/schemas and retained responses visible |

Both pages remained read-only. Before/after digests include record contents and
update timestamps across session, reservation, receipt and budget collections:

- `e62218e7…`: 3 records, unchanged
  `b50c5799acdd4d68a76ddf8e48927bf569e84ddac509de4897048deb217129a6`.
- `f405f9a7…`: 16 records, unchanged
  `ccd125880cf38fc4be4699a19d9f481fa6a1e14def66a5b3b6d10698430f9288`.

Test servers, the fake provider and the dedicated Chromium instance were stopped
after inspection. Existing hosts and stopped sessions were not resumed.

## Limits

This validates protocol wiring and diagnostics, not compatibility or reliability
of the real corporate gateway. Detailed capture must be enabled explicitly in
host configuration. Historical journals do not reliably identify the effective
protocol or per-call price; the viewer labels those values unavailable.

The request view is the retained logical request, not a reconstructed wire body.
Token estimates are diagnostic approximations. Provider usage and possible usage
from uncertain attempts remain distinct. External MCP model internals are only
available when the integration supplies telemetry. Historical trace retention can
remove spans while encrypted journal evidence remains inspectable.

See [usage and storage details](llm-protocol-and-traces.md).
