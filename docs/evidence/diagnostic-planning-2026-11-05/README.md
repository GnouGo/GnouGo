# Diagnostic planning validation — 2026-11-05

This evidence concerns PR #120's existing `feat/incremental-taskplan-construction`
branch, based on `91686f0e40fb407169ae2dfb43167e8cb200b713`. The initial solution and
`.vscode/` edits are unrelated and excluded from the feature commits.

Implementation, activation, the advisory/mandatory limit inventory and official
metadata URLs are in [diagnostic planning](../../diagnostic-planning.md).
[Source hashes](source-manifest.json) bind changed implementation, scripts and tests
to the validation. [Validation results](validation.json) record exact commands,
counts, binary/package hashes and limitations. The source manifest excludes these
report files to avoid self-referential hashes.

The final implementation includes `7504ad96`, which fixes an access regression:
ordinary strict planning must work without a diagnostic operator cookie. Only
diagnostic activation and detailed LLM inspection require that protected window.
The designer now explains the Diagnostic IDE/CLI launch profile in place.

| Check | Source | Result |
| --- | --- | --- |
| Complete .NET solution tests, 33 assemblies | `95346337`, before the access fix | 7,747 passed, 0 failed, 13 skipped |
| Access-fix server/UI regression checks, Release | `7504ad96` | 111 passed, 0 failed, 0 skipped |
| Access-fix focused checks, Debug | `7504ad96` | 70 passed, 0 failed, 0 skipped |
| Complete Release solution build | `7504ad96` | 0 warnings, 0 errors |

The full suite was not repeated after the narrow access fix. The updated Release
checks include both actual-host diagnostic browser coverage and the separate
clarification browser test that skipped in the complete run. Full-run skips were
seven Windows-native PowerShell cases, five opt-in live Copilot/GitHub cases and
that one deterministic browser case. The complete run's original source hashes are
retained separately in [its source manifest](solution-test-source-manifest.json).

## Controlled HTTP provider — actual Agent.Server UI

These are **controlled-provider screenshots**, with no paid inference and no
workflow execution. The browser fixture runs `GnOuGoAgentWebHost.Build`, the real
Blazor page, routing/provider serialization, HTTP retries, encrypted journals,
financial admission and the normal planner/compiler/business-review transitions.
Only the HTTP provider and MCP agent catalog are deterministic fixtures.

- [Active understanding phase](controlled-active-phase.png): phase entry was already
  durable while a delayed provider request was running.
- [Threshold warning permitting continuation](controlled-warning-continues.png):
  calls/tokens/time targets are one; diagnostic planning continued to final review.
- [Expanded LLM exchange](controlled-llm-exchange.png): serialized HTTP request,
  maximum output allocation, usage and retained unknown reservation.
- [Correction loop](controlled-correction-loop.png): an invalid navigation response
  is rejected and a subsequent valid response reaches review.
- [Complete redacted exchange](controlled-call-redacted.json) and
  [browser session identities](controlled-browser-result.json).

The same test verifies ordinary strict plan creation without an operator cookie,
strict blocking, all five logical calls (six physical
attempts including one HTTP 500), exact 128,000-token allocations, skipped discovery
and coalesced review, empty and partial bodies, cancellation during a long call,
reload, full host restart with the same encrypted stores, zero extra dispatches
on reopening, responsive width at 390 px, event deltas, paginated content/search,
complete export, unauthenticated diagnostic activation/inspection and foreign-origin rejection, antiforgery and foreign-key
substitution rejection. It performs no artifact approval or execution.

Reproduce after building the bundled browser tool:

```sh
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true --filter FullyQualifiedName~DiagnosticPlanningBrowserTests
```

The test launches [the browser script](../../../scripts/smoke-diagnostic-planning.mjs)
with an isolated, short-lived operator token. Do not copy bootstrap tokens into
logs or retain them in evidence.

## Live provider — blocked at read-only preflight

There are **no live-provider UI screenshots or new live inference results**.
The retained campaign configuration failed readiness before dispatch:

> The selected campaign configuration is missing, changed or does not match this deployment.

The configuration ID is
`9bcafa16e4cfa77599bd5757901d11c41a9578f9efc36600b90589a81c50d9dd`, campaign
`schema-portability-20261019-incremental`, private deployment
`gpt-6.1-sol-2026-09-29`. Readiness used the frozen prior incident binary under
`~/.codex/live/pr120-gpt61-ed9a3a3e/`; that binary is not evidence qualifying the
new implementation. It exited 134. No configuration, reservation, incident or
replay restriction was bypassed.

[Exact decimal ledger summary](live-preflight-summary.json) records the before/after
read-only equality check. Both inspections retained evidence hash
`79ca579fe0431c32f7c489b6bd76c6e637d4f32b68a19a76bfd19bfae872f501`:
6,938 reserved logical requests, 6,874 receipts, 160 unknown physical attempts,
EUR 413.98214989055111712576930921 known cost and
EUR 157.57430550399738390479699311 reserved cost. The financial ceiling and all
charges/reservations are unchanged. No paid model call was made for this work.

## Validation scope and limits

The server adapter matrix isolates calls, corrections, cumulative tokens, elapsed
planning time, input thresholds, output allocations, recursive tasks and graph
nodes under strict and diagnostic policies. Every advance reloads the encrypted
session and verifies the host capture after host defaults change. Creation recovery
also verifies authenticated maximum allocations and rejects a tampered snapshot.
The broader planner suites exercise the shared discovery/retrieval, clarification,
fragment/checkpoint, schema/enum, progress and semantic policies. Separate tests
retain financial denial, invalid-output evidence, permissions, approvals, execution
budgets, finite transport timeouts and uncertain replay barriers.

Provider tests cover both supported OpenAI endpoints, max reasoning, allocations
above 65,536, official metadata round trips, empty capability lists, pricing
boundaries, cache usage and reasoning as a subset of output. Native AOT checks
source-generated captured metadata, independently frozen provider policy and cache
pricing alongside the existing planner/compiler/receipt/execution corpus. The
published Agent.Server smoke checks encrypted format-11 persistence and exact
receipt recovery; execution journal schema 9 remains unchanged.

The Agent frontend has no test/lint/type-check scripts; its production build and
actual-host browser checks are recorded instead. Windows-only checks and PowerShell
script execution require Windows/PowerShell and are reported separately. The
focused server publish excludes rebundling unchanged MCP tools; it retains the
existing Blazor/EF partial-trim boundary. Existing pinned EF Core Tasks experimental
notices are identified explicitly, with no new warning suppression. No full
multi-platform release, merge or external publication was performed.
