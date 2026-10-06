# Normal export glue and cleanup capacity

The previous approved Amazon run exhausted all 50 finalization steps on compiler-generated `set` exports. Only 307 of 10,000 normal steps had run. Its YAML, approval and execution journal remain unchanged; it is never replayed. [Retained failure](evidence/compact-bindings-2026-10-06/amazon-execution.md).

Implementation commit: `7adab9ee`.

## Correction

Fresh sessions and explicit revisions select the approval-fingerprinted `compact-bindings-v2` profile. Normal exports follow their producers in normal execution. Compatible private copy projections and related output properties share a checked assembly; direct validated references remain direct. Exports depending on actual finalizer results remain after those producers with presence guards. Runtime budgets and accounting are unchanged.

Business operations, observable identities, constraints, shared consumers, scopes, guards and explicit preservation-before-cleanup remain intact. Historical omitted-profile and v1 compilation still use their original lowering, including the existing public overloads. No mapping behavior, executor, planning phase, permission or reconciliation rule changes.

Optional literal step descriptions reach parsed/compiled steps, telemetry, streaming events and encrypted invocation records. New business steps reuse their objectives; technical descriptions contain deterministic purposes. `${...}` remains literal. Descriptions never drive execution or authority, but whole-artifact approval hashing still covers them. Old absent metadata stays absent.

The [planning skill](../.agents/skills/gnougo-planning/SKILL.md) now explains accepted requirements, clarification and bounded MCP discovery/inspection within the existing planning loop, followed by TaskPlan, deterministic compilation, review, approval and execution. These responsibilities do not mandate extra model calls.

## Matched deterministic measurements

An eight-item workflow reads arbitrary typed records, exports two checked views and performs one real lifecycle operation. Both profiles execute with identical independent value assertions. Counts include nested steps.

| Sequential fixture | v1 | v2 |
| --- | ---: | ---: |
| `set` steps | 8 | 5 |
| Total compiled steps | 13 | 10 |
| Workflows | 3 | 3 |
| YAML bytes, including descriptions | 9,107 | 8,449 |
| YAML lines | 339 | 305 |
| Runtime invocations | 55 | 31 |
| Normal steps | 22 | 30 |
| Finalization steps | 33 | 1 |
| Logical journal JSON bytes | 344,212 | 276,384 |

Descriptions account for 492 bytes of v2 YAML. Journal measurements include timing metadata and can vary slightly; they measure the reconstructed logical journal, not physical encrypted storage. The parallel fixture has the same invocation and budget reductions, with journal JSON falling from 337,685 to 269,845 bytes.

At 60 iterations, both sequential and parallel v2 runs return every ordered value, duplicate, nested array and explicit null; cleanup executes once, with one finalization step and no inference. The retained v1 sequential fixture fails at 50 finalization steps and never reaches cleanup. Limits are unchanged. Empty collections also pass.

Read-only recompilation of the exact failed Amazon TaskPlan reproduces its historical YAML byte-for-byte under v1. v2 reduces `set` count 91→87, all steps 136→132 and finalizer steps 9→1; workflows remain 13. Mapping YAML falls from 110,213 to 108,621 bytes before descriptions. Descriptions add 13,014 bytes because that retained plan has long objectives, yielding 121,635 total bytes. This is a compilation comparison, not a successful execution or a rewrite of the saved artifact.

[Exact measurements](evidence/export-glue-2026-10-06/deterministic-measurements.json) · [Retained-plan comparison](evidence/export-glue-2026-10-06/retained-compilation.json).

Reproduce the deterministic gate:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~ExportGlueCompilationTests|FullyQualifiedName~ComposedOutputAvailabilityTests|FullyQualifiedName~FinalizerCaptureTests' --logger 'console;verbosity=detailed'
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~WorkflowTelemetryTests|FullyQualifiedName~WorkflowRunTests'
dotnet test tests/GnOuGo.Flow.Persistence.Tests -m:1 -warnaserror
```

Coverage includes both branches, nested captures, normal/cleanup-dependent exports, failure and cancellation, invalid/missing/null observations, preservation ordering, unknown-completion cleanup prohibition, description serialization and receipt recovery, historical profiles and approval invalidation. The local Browser/Document fixture independently verifies actual visits, XLSX cells, completeness and cleanup using fresh v2 planning.

## Validation and live boundary

The full solution passes **4,634 tests, zero failures and 13 skips** across 33 projects with `-m:1 -warnaserror`. The browser test skipped without `PLAYWRIGHT_MODULE_PATH` then passes in a separate 17-test host/browser run. The remaining twelve skips are the existing seven Windows command cases and five paid Copilot cases. No frontend source changed. Published CLI/Flow.Server Native AOT and Agent.Server trimmed encrypted recovery pass, including literal descriptions, absent legacy metadata, receipts and index rebuilding. Publication uses the existing explicit `PublishAot=true` profile for CLI/server, C# warnings as errors, and the two already audited EF Core Tasks experimental notices; no suppression was added. Initial validation-fixture mistakes and the CLI command missing the global AOT flag are retained in the check evidence. Core, Planning and Persistence Release packages, planning Native AOT and skill validation pass. [Solution results](evidence/export-glue-2026-10-06/solution-validation.json) · [Affected checks](evidence/export-glue-2026-10-06/validation-checks.json). Paid execution requires a new reviewed artifact and explicit revision/hash-bound approval with requirement acknowledgments. The requested live remains one Amazon run with ten products maximum, unchanged permissions, limits and independent execution assertions. No code-review live or cohort expansion is authorized here.

The rechecked campaign upper bound before any new dispatch is **EUR 90.771022 / 150**, including **EUR 2.606753** unchanged unknown reservations. [Ledger](evidence/export-glue-2026-10-06/ledger-before.json). Historical invocations and benchmark evidence remain unchanged. PR #117 stays draft until its full execution acceptance gate passes.


## Fresh Amazon planning and review history

Cohort `exportglue20261006a` freezes source/harness `7adab9ee`, the existing oracles and ten-product bound. Actual Browser/Document zero-inference readiness passed. The single new run is `exportglue20261006a-amazon-1`; it has not executed.

Review retained and corrected four proposal issues under the same cumulative planning budget: missing continuation/completeness handling; model-authored technical assembly/completeness; raw records passed to global interpretation; and Browser's default 200-record page exceeding the projection's 100-record bound. The last correction used `editablePaths: ["/tasks/read_manifest/inputs"]`; only the explicit producer `maxRecords: 100` binding changed. No production code, planner rules, runtime behavior or oracle changed during this live planning sequence.

Revision **11**, artifact **`1557a5d6c72c4c30510b0b4e9b08050ae4b8d5c27dc0bd47545ac98d0df238e1`**, contains typed consumer views, producer-derived completeness guards, all manifest page reads, actual per-product navigation, real workbook writing and one cleanup finalizer. Its three pure record-copy loops compile into projections. It has 73 compiled steps, eight workflows and 47 `set` steps; YAML is 96,810 bytes / 2,859 lines. This is a different TaskPlan from the historical failed run and is not a matched size comparison.

Planning: **seven calls/attempts, zero automatic repairs, two discovery reads, four review revisions**, 93,000 input / 26,785 output verified tokens, **EUR 1.125699**, 317,835 ms active planning latency. The initial plan used three calls; this live does not meet a one-call target. Execution inference, journal growth, request sizes, runtime steps and cleanup usage remain unmeasured. There is no completed live oracle yet.

The campaign upper bound after planning is **EUR 91.896721 / 150**, including the same EUR 2.606753 historical unknown reservations. [Accounting](evidence/export-glue-2026-10-06/ledger-after-planning.json) · [Six-slot report; all execution slots remain incomplete](evidence/export-glue-2026-10-06/cohort-before-execution.json).

[Review the accepted requirements beside the actual work](evidence/export-glue-2026-10-06/amazon-review.md). This new artifact requires its own explicit revision/hash-bound approval and all five requirement acknowledgments before the one authorized execution. Prior approvals remain unchanged. No historical or uncertain invocation is resumed; no code-review evaluation or cohort expansion is performed.

Reproduce inspection or execute only after the explicit approval, using the frozen checkout and existing pinned configuration:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --run exportglue20261006a-amazon-1 --max-products 10 --workspace /Users/a115vc/Desktop/GnOuGo
# Only after the user submits the matching approval and requirement acknowledgments:
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability execute --campaign schema-portability-20261002 --cohort exportglue20261006a --case amazon --run exportglue20261006a-amazon-1 --max-products 10 --workspace /Users/a115vc/Desktop/GnOuGo --review-command /path/to/explicit-user-approval.json
```

The harness refuses replay after execution starts. Public evidence includes each proposal, exact YAML, newly issued model responses and revision command. Full original schemas, requests, durable receipts and revision history remain in the encrypted campaign journal. Latest CI snapshot shows stable .NET/Python, proxy AOT and standalone checks passing; some host/package jobs are still running. [CI snapshot](evidence/export-glue-2026-10-06/ci-status.json).


## Approved execution outcome

The user approved revision 11 and all five requirements; it executed once with unchanged YAML on frozen `7adab9ee`. **The live failed**, with `LLM_BUDGET_EXCEEDED` before the consent mapping reached the provider. All 52 manifest pages / 1,488 records were consumed; the whole-collection mapping still exceeded admission (97,193 estimated prompt tokens; 181,033 for the complete serialized request versus 96,000 allowed). There were no product visits or XLSX. The existing oracle reports `workflow_execution_failed` and `workbook_missing`.

**Cleanup capacity is fixed in this live failure path:** 1,169 normal steps and one finalization step, with verified Browser cleanup. No unknown completion, historical replay, oracle relaxation or additional paid evaluation occurred. Execution used one paid URL-construction call, 116 input / 342 output tokens, EUR 0.009619 and 63,420 ms. Campaign upper bound is **EUR 91.906340 / 150**, including unchanged unknown reservations.

The logical journal is 373.42 MB; its checkpoint is 0.359 MB and currently referenced immutable block JSON is 11.85 MB (excluding superseded blocks and encryption/database overhead). Full reconstruction remains expensive; these figures are not checkpoint-write latency or a matched before/after comparison.

[Full execution report, remaining generic composition issue and CI limitation](evidence/export-glue-2026-10-06/amazon-execution.md). PR #117 remains draft with **0/6 execution gates**. The completed failed invocation is retained and will not be replayed.
