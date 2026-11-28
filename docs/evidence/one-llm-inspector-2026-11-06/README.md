# One LLM inspector — 2026-11-06

Implementation is on the existing PR #120 branch. The original ten local commits,
staged solution/VS Code files, session captures and retained incident are preserved.
No PR merge, release, live inference or budget increase was performed.

## Result and activation

Restart Desktop or run `dotnet run --project src/GnOuGo.Agent.Server`. The normal
protected operator window remains the default; `--launch-profile Diagnostic` is
an alias. Warning authority does not imply maximum output allocations. New sessions
use configured navigation/drafting/review allocations, including phase overrides;
`TypedWorkflowPlanning:DiagnosticModelMaximum=true` is an explicit host opt-in.
Existing captures remain unchanged. See [activation and limit inventory](../../diagnostic-planning.md).

Planning retains engine phase/block, overallProgress, warnings and blocking causes.
Timeline request links open the same TraceSidebar → TracePipeline → LlmCallDetail →
LlmTraceDocument used by chat. Exact tenant/session-owner/request identities join
journal evidence and real spans once, with HTTP attempts inside. Session calls span
all owned traces; raw span/log selection remains independent. Response receipt,
technical validation and business review are distinct. Journal fallback has an
explicit source and invents no spans, duration, usage or acceptance.

OpenTelemetry remains observability. Existing encrypted journals remain authoritative
for I/O, recovery and financial reservations. Engine state owns progress. Existing
KeyVault storage supplies metadata cursors and authenticated chunks, not a new
service, synchronization worker or planning/progress cache. No summarization call
is added. Mandatory semantics, Judges, permissions, approvals, workspace confinement,
financial policy, cancellation, finite network timeouts and uncertain replay barriers
remain unchanged.

## Bounded reads and persistence

Metadata queries scope tenant, collection and exact session prefix. Call refreshes
use independent request/receipt/HTTP cursors, bounded engine-event deltas, selected
requests and active attempts. Old events cannot expand the initial history page.
UTC ticks serialize as strings to survive JavaScript round trips exactly. Only the
selected request's accounting is projected; complete session budgets are not exported
with each call. Unknown historical costs remain unavailable.

Large content is redacted before chunking. The viewer fetches pages on demand;
search has bounded server continuation, and complete redacted export streams
separately. Every protected read verifies ownership and document version. Full-record
replay reconstructs identical logical bytes. Explicit historical access may hydrate
once to prepare pages; original timestamps, charges and reservations remain unchanged.
Transactions publish chunks atomically and preserve read snapshots across concurrent
writers. A write-contention regression reproduced an overwritten value before the
snapshot correction and passes afterward.

## Validation

The adjacent reports bind each check to its source commit. The full solution run
finished on frozen source `7169a227`; subsequent fixes and focused checks are
recorded separately. Focused rerun counts are not added to full-run totals.

| Source | Check | Result |
| --- | --- | --- |
| `7169a227` | Full solution, 33 test assemblies | **7,800 passed, 2 failed, 12 skipped** |
| `7169a227` | KeyVault storage suite | **19 passed**, including the reproduced concurrent-writer regression |
| `e2d3e01a` | Affected Server suites, including actual-host inspector browser scenarios | **139 passed, 0 failed, 0 skipped** |
| `e2d3e01a` | Complete browser MCP suite | **142 passed, 0 failed, 0 skipped** |
| `62e3e6de` script / `e2d3e01a` binaries | Actual-host clarification, reload and cancellation browser test | **1 passed, 0 failed, 0 skipped** |
| `e2d3e01a` | Full Release solution build with warnings as errors | **0 warnings, 0 errors** |
| `7169a227`, unchanged frontend sources afterward | Frontend production build | **Passed** |
| `e2d3e01a` | Native AOT, published Server storage/replay smoke, three package checks | **All nine steps passed** |

The two full-run failures were test-fixture regressions and remain in
[the complete result](solution-validation-results.json):

- `RepeatedNavigationStopsAfterThreeAttemptsWithNoPartialResult`: cold browser
  initialization consumed the observation deadline during concurrent publishing.
  Commit `af51549a` warms the fixture before measurement, preserving the original
  five-second deadline, three-attempt and no-partial-result assertions. The complete
  142-test browser rerun passes.
- `DesignerBrowserUsesRealPersistedQuestionsAndExplicitSubmission`: the cancellation
  locator matched both the session status and the engine timeline. Commit `62e3e6de`
  scopes the assertion to the live session status; its actual-host rerun passes.
  This last commit changes only the browser script loaded at runtime; compiled
  product sources remain those qualified at `e2d3e01a`.

The confirmed CI port conflict is fixed with Kestrel-owned loopback port-zero
listeners. No probe-and-release reservation or test suppression remains in the
affected fixtures; deadline and cancellation assertions are retained.

See [final validation summary](final-validation-summary.json),
[focused commands](followup-validation-results.json),
[clarification rerun](clarification-validation.json),
[publication commands](publication-results.json), and
[source hashes](final-source-manifest.json). Compressed logs have
[recorded raw and archive hashes](log-hashes.json). The earlier `a523104a` run was
stopped after finding the storage race and is explicitly
[superseded](superseded-a523104a.json).

The already-running `1a81a769` baseline is separately recorded as
[7,787 passed, 1 failed, 12 skipped](../unlimited-llm-budget-2026-11-05/README.md).
Its staged-tool path fixture failure is fixed here. The older `51fed8a2` baseline
remains [7,765 passed, 0 failed, 12 skipped](../default-diagnostics-2026-11-05/README.md).
Neither is claimed as validation of this refactor.

Controlled-provider browser evidence covers long calls, two HTTP attempts inside
one logical call, warnings, invalid/empty/partial responses, correction loops,
reload, cancellation, paged viewing, UI/API search, streamed export, authorization
failures, version changes and real cross-session substitution. Restart with an
empty telemetry database retains five journal calls and unchanged accounting, with
zero fabricated spans. Evidence is labelled controlled-provider; no live-provider
UI evidence is claimed.

Controlled-provider screenshots:

- [Active phase](controlled-active-phase.png)
- [One logical LLM exchange with nested HTTP attempts](controlled-llm-exchange.png)
- [Warning that permits continuation](controlled-warning-continues.png)
- [Protected trace-panel inputs and outputs](controlled-otel-input-output.png)
- [Journal fallback after restart, with no telemetry spans](controlled-journal-fallback-after-restart.png)
- [Correction loop](controlled-correction-loop.png)
- [Mobile layout](controlled-mobile.png)

The [browser assertions](controlled-result.json) and
[restart assertions](controlled-restart-result.json) accompany the screenshots.

Native AOT and published encrypted-storage checks exercise exact replay, Unicode
boundaries, restart, ownership and source-generated cursor serialization. Packages
are local qualification artifacts. Existing publish-local Jint/partial-trim boundaries
remain; no new warning suppression is introduced. Full solution compiler warnings
remain errors. The two existing EF Tasks experimental-feature notices are retained
in the published-server report.
The frontend has no separate test, lint or type-check package scripts; those checks
are reported as unavailable rather than claimed as passing.

## Live-provider limitation

Read-only current-source readiness fails before inference with:
**The selected campaign configuration is missing, changed or does not match this deployment.**
The frozen retained-campaign inspector verifies identical before/after accounting
and evidence hashes; it is explicitly not current-source qualification. The existing
ceiling and all uncertain reservations remain unchanged. No configuration or replay
barrier was bypassed. Native Photino window automation and other operating systems
are not covered by the actual Agent.Server browser scenarios.
