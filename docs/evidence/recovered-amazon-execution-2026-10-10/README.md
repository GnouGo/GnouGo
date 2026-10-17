# Approved recovered Amazon execution: persistence failure

Run `physicaloutputs20261010a-amazon-1` was executed **once** after the user's
explicit approval of the recovered workflow. It failed; no product page was
visited and no workbook was created. PR #117 remains draft.

## Approval and frozen execution

The [reviewed revision 6](../schema-pruning-2026-10-10/amazon-review.md) and artifact
`ec011e1deed9ee48dd413d17911351ae2a08c8222a08efb271f6d84771ff86e0` were unchanged.
The five requirement acknowledgments were submitted through the existing approval
command, producing revision 7, `approved`. See [approval.json](approval.json).

Execution used the original frozen candidate
`7376b4bd713d72d43dbe62270e41823273e0e618` from its original worktree. Receipt
recovery using validator `847a15b9` remains separately recorded; the historical
manifest was not rewritten and the live harness's build checks were not bypassed.
There was no new planning call, revision, model-call replay or changed YAML.

Configured pricing, exact-model allowances and currency readiness passed before
execution. The admitted ECB USD/EUR quote was dated 2026-10-06, within the existing
freshness policy. Input allowances remained 96,000 tokens and the campaign ceiling
remained €150. The original run had never started before this authorization.

The single execution command was:

```sh
# From the original frozen worktree; evidence only, DO NOT run again.
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability execute --workspace /Users/a115vc/Desktop/GnOuGo \
  --campaign schema-portability-20261002 --cohort physicaloutputs20261010a \
  --case amazon --run physicaloutputs20261010a-amazon-1 --max-products 10 \
  --review-command /private/tmp/physicaloutputs20261010a-r6-approval.json
```

## Separate results

| Stage | Observed result |
| --- | --- |
| Planning/recovery | Four prior logical calls, zero repairs; committed fourth response already recovered; approval succeeded without additional planning. |
| Search acquisition | Complete snapshot: 52 pages, 1,501 records; capture and manifest truncation both false. |
| Navigation recovery | Two acquisition attempts; one navigation invalidation discarded the previous generation. The last verified GET returned 200; current-generation HTTP status remained unknown. |
| Independent extraction | All 52 pages processed and validated. One mapping generation, three complete examples, 49 unsampled pages still executed; zero program repairs or specializations. |
| Product selection/visits | Execution stopped in generated precondition plumbing before the selection model call. Zero individual product visits. |
| Excel | No Document writer call, no `products.xlsx`. |
| Workflow cleanup | Did not run after persistence failed. The oracle detected the still-open Browser, then its separate safety cleanup returned `closed: true`. |
| Independent E2E oracle | Failed: `workflow_execution_failed`, `browser_not_closed_by_workflow`, `workbook_missing`. |

The mapping request was 73,334 bytes. Mapping output was 30,062 bytes, with
1,755/10,000 sandbox statements, 23.8389/5,000 ms sandbox execution,
11,200,992 allocated bytes and 5,906,674 materialized bytes under the unchanged
50,000,000-byte ceiling. Mapping therefore passed its full-collection validation;
it was not the failing component. These counters exclude provider latency.

The execution lasted approximately **479 seconds**. See the exact
[execution result, telemetry and oracle](execution-result.json).

## Exact failure and retained state

`System.OverflowException` originated at `JsonNode.ToJsonString()` in
`EncryptedWorkflowRunStore.SplitJournal.EncodeAsync`, line 158 in the frozen
candidate. The writer first serializes the entire subtree to decide whether it
fits its inline threshold, even for a forced block. That defeats bounded splitting
for very large logical snapshots. No sandbox limit was raised or bypassed.

The last committed checkpoint has revision/event count 139 and 41 invocation
records: 39 completed and two locally dispatched (`switch` and `set`). All provider
calls have completion receipts. This does not make the workflow journal terminal:
the failing receipt could not be committed, and this run must not be replayed.

Read-only inspection followed immutable block references through public KeyVault
APIs, validating tenant/run ownership and block hashes. It calculated logical JSON
sizes without reconstructing the large values:

- Last dispatched scalar `set` `n_caaabe4079e045d9`: **699,699,470 bytes** of
  `dataBefore` for a boolean selection.
- Previous completed scalar `set`: **349,849,720 bytes** in each before/after
  snapshot, with a **14-byte output**.
- Sum of the 41 invocation records when logically expanded: **7,332,375,627 bytes**.
- The 255 unique blocks reached from these invocation records contain
  **1,552,883 plaintext envelope bytes**. This measures deduplicated record content,
  not encrypted database/file size; header/event-only blocks are excluded.

The existing split layout deduplicates storage, but whole-tree serialization and
cloning still expand the logical state. A [running stack sample](running-stack.txt)
also caught `WorkflowRunJournal.PrepareAsync` in recursive `JsonNode.DeepClone`.
See [checkpoint](checkpoint.json) and [measurements](journal-measurements.json).

The smallest next correction is inside existing persistence: determine inline
eligibility with bounded traversal and write container blocks without serializing
the whole container first. Preserve exact JSON, atomic checkpoint/receipt ordering,
ownership checks and reconciliation. Add a zero-inference regression using this
retained repeated-state shape before authorizing another run. Snapshot cloning
remains a separate measured cost; this evidence does not claim it is fixed.

No implementation change was made during this execution or inspection.

## Accounting and validation

- Runtime: **2 logical calls / 2 attempts**, 36,323 input and 1,737 output tokens;
  **€0.2074052710977016594196468187**; zero new unknown attempts.
- Prior planning is unchanged: four logical calls / five transport attempts,
  58,273 input and 20,341 output tokens; €0.8000665542639098411571568019.
- Campaign upper bound: **€122.58984383913018575627554166 / €150**.
- Unknown reservations remain **€6.5016147947905255187922165013** across five
  historical transport attempts. All three retained inconclusive logical requests
  remain unchanged; none was replayed or released.

See [accounting-after.json](accounting-after.json). Monetary JSON tokens are
preserved without conversion through binary floating point.

The read-only [inspection source](inspect-checkpoint.cs) builds warning-free as a
temporary net10.0 console project referencing Flow.Planning and Flow.Persistence.
Run it with the existing workspace and `--sizes`; it performs no writes, inference
or workflow execution. It inspects this run only and is evidence, not a runtime
component. No large snapshot or private request payload is committed here.

No production/tests changed, so the existing
[offline validation](../schema-pruning-2026-10-10/validation.json) remains separately
reported: 5,061 passed, one unrelated Copilot teardown failure, 13 skips. The
[CI snapshot](ci-at-execution.json) at `4201c5b4` has 21 successes, eight still
running and three skips; it is not a claim that all CI has finished.
