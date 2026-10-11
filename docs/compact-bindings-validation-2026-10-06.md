# Compact bindings: validation and fresh Amazon boundary

The failed cursor live is retained separately in [its report](cursor-live-2026-10-06.md). Evidence commit `5cb6b60a` preserves the approved r8 YAML and command exactly. That execution is never resumed.

Implementation commits `39210589` and `0348550a` introduce approval-fingerprinted compact lowering, opt-in inference of independent runtime extraction collections, isolated business loops, and preservation of the first journal exception. New hosts omit the redundant startup confirmation; artifact approval, MCP permissions, questions and explicit cookie handling remain. The planning skill shrinks from 22,641 to 6,327 bytes. [Compilation and compatibility](compact-workflow-bindings.md).

## Deterministic measurements

The same two guarded sequential loops use identical inputs and independent output checks under legacy and compact compilation. There is no provider inference.

| Eight-item fixture | Legacy | Compact |
| --- | ---: | ---: |
| Collected result bytes | 1,450,586 | 3,794 |
| Logical snapshot bytes | 13,185,824 | 698,226 |
| Runtime invocations | 88 | 90 |
| YAML bytes | 14,461 | 17,691 |
| Workflows | 3 | 5 |

Isolation adds two scope calls and explicit contracts, while removing preceding-state amplification. Compact collected bytes are 1,898 for four items and 3,794 for eight. Snapshots still include captured inputs and grow with actual work; they are not pruned or buffered asynchronously.

A separate copy-only fixture shrinks from 13,765 characters / three workflows to 11,737 characters / one workflow. It processes every item, duplicates, explicit nulls and nested arrays with no per-item invocation or inference, including large unrelated source fields.

A read-only recompilation of historical r8 reduces steps from 223 to 195 and workflows from 18 to 17, but increases YAML from 170,243 to 189,294 bytes because isolated scopes carry contracts. Its ten explicitly authored interpretation transforms remain interpretation. This comparison does **not** claim that compilation alone simplifies that old business composition. A fresh plan must choose extraction where appropriate; no saved plan or approval was rewritten.

[Measurements](evidence/compact-bindings-2026-10-06/deterministic-measurements.json) include exact counts. Reproduce the matched execution fixture with:

```sh
python3 docs/evidence/compact-bindings-2026-10-06/measure.py --root "$PWD"
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter FullyQualifiedName~CompactBindingCompilationTests --logger 'console;verbosity=detailed'
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~DynamicMappingCollectionTests|FullyQualifiedName~WorkflowRunTests'
```

Development validation caught a rounded JSON number in counted-loop indexing. The retained regression fails before the structural indexed-read correction and passes afterward. It also confirms that unrelated captured data need not enter the JavaScript sandbox. No numeric arithmetic policy changed.

## Validation status

Candidate `0348550a` passes 1,111 Core tests, the affected Release packages, the updated planning Native AOT smoke and published CLI/Flow.Server encrypted recovery. **The final solution run passes 4,601 tests, zero failures and 12 existing skips across 33 projects**, with `-m:1 -warnaserror`. Seven skips are Windows-only commands and five are separately authorized paid Copilot cases. The real browser review smoke and local product-to-XLSX fixtures are included. [Solution evidence](evidence/compact-bindings-2026-10-06/solution-validation.json). Agent.Server's published format-10 planning/mapping recovery smoke also passes; journal schema 9 is unchanged.

Release packages and planning AOT use `-warnaserror`. CLI/Flow.Server/Agent.Server publication uses the existing supported profiles with C# warnings treated as errors; EF Core Tasks emits its two existing experimental-feature notices. The initial global-warning-as-error CLI attempt and an incorrectly targeted HTTP smoke against Agent.Server are retained as development command failures. The corrected HTTP smoke targets Flow.Server and passes unchanged assertions. No warning suppression or test oracle was added or weakened.

## Fresh live boundary

Only one new Amazon E2E is included, with **ten products maximum**. Cohort `compactbindings20261006b` freezes production/harness `0348550a`, model configuration, the explicit ten-product bound and existing execution assertions. The earlier `compactbindings20261006a` identity contains readiness only, with zero model calls.

Zero-inference readiness confirms the exact configured model allowances, local Browser cleanup, actual document writing and independent XLSX reading. Campaign `schema-portability-20261002` remains at **€88.87480257548656 / €150** before dispatch, including **€2.6067527839643653** in unchanged unknown reservations. [Readiness](evidence/compact-bindings-2026-10-06/provider-readiness.json), [ledger](evidence/compact-bindings-2026-10-06/ledger-before.json).

Fresh generation must pass the deterministic gates first. Its own revision/hash-bound artifact approval and explicit requirement acknowledgments are required before execution. There is no new code-review live, cohort expansion, automatic consent acknowledgment or replay of uncertain invocations. PR #117 remains draft.

## First fresh proposal and guarded-copy correction

Cohort `compactbindings20261006b` remains unexecuted and unapproved. Its initial proposal reached review in two calls without repairs but ignored read truncation. Two explicit implementation revisions retained completeness guards, actual product visits, extraction, document writing and cleanup; all proposals and feedback are retained. Total planning: four calls/attempts, zero automatic repairs, two review revisions, 52,644 input / 18,694 output tokens, EUR 0.731245, 250,106 ms. The campaign upper bound is EUR 89.606048, including the unchanged EUR 2.606753 unknown reservations. No execution/model call for execution occurred.

Review then exposed an optimization gap: an entry requirement on a copy-only loop prevented fusion, despite the guard already being emitted before the collection. Commit `11e8e6d1` removes only that exclusion. Per-item guards, effects, inference and cleanup still prevent fusion. Two regressions fail before the correction and pass afterward: both sequential and parallel loops preserve every value with no per-item invocation; false entry requirements execute only the guard and publish no result. All sixteen related regressions pass.

A read-only recompilation of the retained r6 proposal shrinks from 98,442 to 85,393 bytes and 17 to 11 workflows. Its approved/stored artifacts are not replaced (no approval was issued for this proposal). This remains compilation evidence, not execution success. [Retained cohort](evidence/compact-bindings-2026-10-06/cohort-b-before-execution.json), [regression evidence](evidence/compact-bindings-2026-10-06/guarded-copy-regression.json).

The one requested live execution will use a fresh identity on the corrected frozen candidate after validation. The earlier cohort is not pooled with that candidate.

## Corrected candidate gate

Frozen production/harness `71315a5a` contains the guarded-copy fix and a test-only assertion-style correction. The final solution rerun passes **4,603 tests, zero failures, twelve existing skips**, with `-warnaserror`; [per-project results](evidence/compact-bindings-2026-10-06/solution-validation-final.json). The preceding run passed all behavioral assertions but failed on `xUnit2031`; the predicate-overload correction changes no test expectation. Planning Release packaging and Native AOT were rerun for the guard correction. Core/Persistence and published recovery code remain unchanged from their earlier successful checks.

Cohort `compactbindings20261006d` uses the corrected frozen candidate and a fresh planning/execution identity. Cohorts `compactbindings20261006a` and `compactbindings20261006c` contain readiness only, with zero inference. [Corrected readiness](evidence/compact-bindings-2026-10-06/readiness-d.json) confirms actual Browser/Document operations against disposable local data and independent XLSX reading. No saved execution is replayed.

## Fresh Amazon ready for review

Run `compactbindings20261006d-amazon-1` reached final review at revision 8, artifact `0e86683a295778310311f8acda2d3c88a9b771a50cfdca30e940193fe24aad9f`. Two rejected proposals and both review commands remain retained. The reviewed composition uses producer completeness guards, four fused record-copy projections, actual product visits, explicit observed-field extraction, actual workbook writing and root cleanup. There is no startup confirmation. [Concrete review and requirement acknowledgments](evidence/compact-bindings-2026-10-06/amazon-review.md).

Planning used six calls/attempts, two repairs, two review revisions and two discovery reads; 66,235 input / 24,537 output tokens, EUR 0.947098 and 288,310 ms. At review time the campaign upper bound was EUR 90.553146 / 150 with unchanged unknown reservations. The original review and artifact are retained unchanged.

## Approved execution result

The user subsequently approved revision 8 and all four requirements. The workflow executed **once and failed** after 45,348 ms. The compiler's normal output exports consumed all **50 finalization steps**, leaving no allowance for Browser cleanup; only 307 of 10,000 normal steps had run. Search-page consumption stopped at 17 of 53 declared pages. No product visits, dynamic mapping or workbook writing occurred. The independent oracle failed and its separate teardown closed Browser. The run will not be replayed.

Two runtime calls used 46,735 input / 395 output tokens and EUR 0.217876, with verified receipts. Campaign upper bound: **EUR 90.771022 / 150**, including unchanged unknown reservations. The journal reconstructed successfully: 72.92 MB logical JSON, 3.20 MB active payload-record JSON and an 86 KB checkpoint; these exclude superseded blocks and encryption/database overhead. No production code, limits or oracles changed during this execution. [Exact failure, diagnosis, accounting and retained evidence](evidence/compact-bindings-2026-10-06/amazon-execution.md).

PR #117 remains draft: **0/1 attempted execution oracles passed; five cohort slots unexecuted**. A deterministic correction for normal export glue consuming finalization allowance is required before another approved live.
