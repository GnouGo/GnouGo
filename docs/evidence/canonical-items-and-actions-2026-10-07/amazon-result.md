# Fresh canonical-mapping/action-reference evaluation

**Planning reached review; the single execution failed before runtime provider dispatch.** This is not live acceptance of learned mapping or Browser interactions.

Frozen production/harness: `789fda4e04021a114ff3fbb04b9fb4465854e012` (production correction `da8e4b50`, test-fixture teardown follow-up `789fda4e`). Cohort `canonical20261007b`, run `canonical20261007b-amazon-1`, ten products maximum. The [manifest and six-slot report](cohort-report.json) retain source/harness/configuration/prompt/oracle hashes and unchanged limits. The earlier `canonical20261007a` manifest was created by zero-inference readiness before the test-only follow-up; no paid run used it.

## Planning and approval

- Revision 2 stopped on nonboolean `requires`; review also found that `query` was not actually used by search and raw snapshots fed interpretation. The proposal and explicit revision feedback are retained.
- Revision 4 still had nonboolean guards and `each` names that did not identify existing inputs/results. The next explicit revision preserved requirements and business operations and removed invented extraction metadata.
- Revision 6 stopped because a whole loop-result envelope was passed instead of its declared product array. A targeted revision authorized only `/tasks/visited_products_value/outputs/visited_products`.
- Revision 8 reached final review. Exactly one binding changed from revision 6: its port became `product`. All requirements and unrelated work remained unchanged.

[Manual artifact review](amazon-review.md) individually acknowledges all five accepted requirements against revision 8, hash `587be0412c9edb42b9fe1aa518cabfcfe994146979f7b5d45db28d045c065e30`. The user's authorization for this next validation was applied using the existing [approval command](approval-r8.json); stored revision 9 records approval. No old approval was reused. Generated [YAML](amazon-r8.yaml): 228,152 bytes / 6,256 lines. This focused change did not undertake another compiler simplification.

Planning used five calls, two discovery reads, zero automatic repairs and three explicit revisions. These are cumulative counters; no budget was reset. This does not demonstrate one-call live planning.

## Execution evidence

1. Real Browser complete acquisition of the home page succeeded: **131 records, three pages, one acquisition attempt, no invalidations or truncation**. Records exposed observed references and native/ARIA action compatibility: 97 follow-capable records, six activation controls, one fill control and one select control (press overlaps these counts).
2. The first independent extraction prepared all three complete page examples in a **61,217-byte** mapping request. The retained inspection estimate is **30,923 prompt tokens**, below the unchanged 96,000 input allowance. It was a cold-cache invocation, one local inference attempt, zero repairs, and zero successfully processed/published items.
3. Campaign preflight failed with **`currency_quote_unavailable`**. `KeyVaultBenchmarkModel.DispatchAsync` requests the currency quote before creating the HTTP journal/reservation and before invoking the provider. Retained failure stage and accounting show **zero runtime provider calls, attempts, tokens and cost**. No script or completion response was produced.
4. The ordinary preflight exception left no terminal Flow mapping receipt. The retained invocation is `needs_reconciliation`, and Flow returned `RUN_NEEDS_RECONCILIATION`. This is a separate preflight-error/receipt-classification gap; no mapping algorithm, permission or limit was weakened to avoid it. The failed run was not reconciled or replayed.
5. Runtime finalization was blocked by that unresolved invocation. The independent oracle confirmed that the page was still open, then performed its existing defensive Browser teardown. That teardown is not workflow cleanup success. There were **no reference-based interactions, search submission, product visits, learned scripts or XLSX**.

[Sanitized execution evidence](amazon-execution.json) retains the exact runtime failure, preflight cause, inference receipt state, mapping telemetry, structural acquisition counts and oracle findings. Full observations and issued requests remain encrypted. The unchanged oracle failed with `workflow_execution_failed`, `browser_not_closed_by_workflow`, and `workbook_missing`.

| Stage | Logical / physical provider calls | Input / output tokens | Cost EUR | Latency ms |
| --- | ---: | ---: | ---: | ---: |
| Planning | 5 / 5 | 64,662 / 21,510 | 0.859535 | 220,800 |
| Execution | 0 / 0 | 0 / 0 | 0 | 86,807 |
| Total | 5 / 5 | 64,662 / 21,510 | 0.859535 | 307,607 |

The execution telemetry's one *local mapping attempt* is distinct from dispatched provider calls. There is no verified runtime completion receipt; zero paid usage follows the separately retained pre-dispatch failure, not an assumption that missing receipts are free.

Campaign upper bound: **EUR 96.942719 / 150** = EUR 94.335966 known cost + unchanged EUR 2.606753 reservations for two historical unknown provider completions. Remaining upper-bound allowance: EUR 53.057281. No reservations were released. The new unresolved Flow invocation remains untouched even though campaign preflight records no provider dispatch.

## Validation and remaining work

[Deterministic validation](validation.json): full solution with warnings as errors **4,709 passed, zero failed, 12 existing skips**; 77 mapping tests, 88 Browser tests and 682 host tests are included. Seven focused real MCP/Browser/Document/receipt cases also pass against the self-contained Browser binary. Flow.Core/Planning Release packages, planning Native AOT and skill validation pass. Earlier compilation assertions and fixture-disposal failures remain recorded separately, with their corrections and successful reruns.

The frozen candidate's Linux CI host job failed one valid-action receipt case without an informative runtime cause; the same case passes locally and in the published-binary check. Test-only follow-up `4d31fe69` adds the error code/message to the assertion, and all three receipt cases pass locally. CI follow-up status is recorded separately; no timeout or assertion was relaxed.

No further paid run, code-review evaluation, cohort expansion or uncertain replay occurred. The six-slot live report remains **0/6**, one failed execution and five unstarted slots. Historical cohorts and the 33/33 benchmark remain unchanged. PR #117 stays draft.

A future evaluation needs reliable campaign currency admission and a durable, typed pre-dispatch failure path, plus resolution of any repeated Linux CI failure. Neither availability nor live mapping/action execution is established by these deterministic successes.

## Reproducible commands

From a clean checkout of the frozen candidate, build the existing benchmark and MCP projects:

```bash
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror -p:SkipModelMetadataGeneration=true -p:SkipClientBuild=true
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability plan --case amazon --run NEW_UNUSED_RUN_ID \
  --cohort NEW_FROZEN_COHORT --max-products 10 \
  --campaign schema-portability-20261002 --workspace "$GNOU_GO_WORKSPACE"
```

Use the existing `inspect-run`, `revise --revision-command <file>`, `execute --review-command <file>` and `report` commands. New paid execution needs its own authorized scope, budget admission and concrete artifact review. These retained approvals authorize only the already-started run; they must not be reused.
