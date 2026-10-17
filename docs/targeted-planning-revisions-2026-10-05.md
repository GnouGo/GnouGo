# Targeted planning revisions preserve business work

**Latest status:** the user approved revision 10 and its five requirements. One execution completed with `LLM_BUDGET_EXCEEDED`: all 44 initial pages were consumed, but the declared compact export still produced an oversized global request. Browser cleanup passed; no product visits or workbook. No invocation is uncertain and nothing was replayed. The [execution evidence](evidence/targeted-planning-revisions-2026-10-05/execution-result.json) is separate from the passing deterministic gates and earlier planning-only checkpoints.

The retained [Amazon proposal](evidence/compact-interpretation-2026-10-05/rejected-final-r12.json) removed the workbook writer during a full implementation revision. Its final repair replaced the missing writer result with a literal path. This correction starts from `11e0ddd0`; historical artifacts and invocations remain untouched.

## Correction

`revise` accepts optional `editablePaths` together with `preserveRequirements: true`, the expected revision and current artifact hash. Supported targets are existing binding/export values, complete input lists and conditions. The host validates distinct, unambiguous, nonoverlapping paths before inference. It retains the TaskPlan and emits the existing typed patch envelope for those slots. Tasks, operations, objectives, ordering, scopes, extraction semantics and unrelated cleanup cannot be regenerated. Structural restructuring still requires a separate explicit global revision. A targeted revision consumes a planning call; automatic repairs retain their own existing ceiling. Invalid patches preserve the baseline and stop for explicit correction.

The existing session stores optional `EditablePaths` encrypted with its baseline. Absent fields are omitted; TaskPlan, PlanningGraph and storage format 10 remain unchanged. The optional field passes through the existing Designer command DTO; originating chat keeps its narrower decision-only authority. No new UI editor, runtime component, IR, executor, model phase or mapping behavior is introduced.

Automatic reference repairs now offer only compatible visible ports of the referenced producer, or the existing authorized export chain. A missing producer stops with `REVISION_REQUIRED` before a repair request. Constants and unrelated same-typed values cannot replace missing work. Explicit business literals and contract defaults remain available in their proper contexts. This is dependency preservation, not proof that arbitrary prose has been implemented.

Fresh private envelope 8 fingerprints complete permissions and explicit revision authority. Already-issued envelope-7 schemas, fingerprints and completion receipts remain usable under their original profile. Approvals are invalidated by revision and must be obtained again for the exact artifact.

## Regressions and validation

The sanitized retained revision is replayed with arbitrary operation/task names. A dropped writer is rejected; a correction limited to aggregation inputs and guards preserves the writer, exports and cleanup. Additional tests cover immutable unrelated work, invalid/stale paths, compatible-port-only repair, missing producer rejection, explicit literals, tenant isolation, restart, historical request replay and unchanged cumulative counters.

The local `pages-compact-scoped` scenario executes through real Flow, Browser and Document MCPs after a targeted input-list/guard correction. Its independent oracle checks actual product visits, XLSX cells and Browser cleanup. Existing observation, compaction, denial and failure cases remain unchanged.

This fixture also exposed a compiler omission for a whole-value `present` guard. Final lowering now uses the existing checked boolean `set` path, as predicates already do. No runtime or mapping code changed. Both presence outcomes are covered, including zero downstream calls when the guard fails. Composite repair tests also reject reusing a neighbouring legitimate literal as missing producer evidence; schema factoring alone does not authorize recomposing the binding.

Reproduce focused tests:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~TargetedRevisionTests|FullyQualifiedName~RepairPatchRuntimeTests|FullyQualifiedName~ScopeReferencePlanningTests'
Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipModelMetadataGeneration=true --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~PlanningPersistenceTests'
```

The focused gate passed **109 planning tests** and **12 host tests**, including the real workbook fixture and encrypted persistence. Final validation on frozen candidate `5afe0fa6` passed:

- **4,574 solution tests**, zero failures, 12 existing skips across 33 suites, with `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true`. Seven skips require Windows; five are opt-in paid Copilot tests. The existing browser component check ran using the packaged Playwright module, and the frontend production builds passed.
- Release packages for Flow.Core, Flow.Planning and Agent.Shared.
- **29 Native AOT planning checks** on `osx-arm64`, including explicit revision authority/patch serialization and immutable baseline checks.
- Published, trimmed Agent.Server encrypted recovery, including the new optional scope field and unchanged planning format 10. Unchanged bundled MCP publishing was skipped for this host persistence check; current Debug MCP processes were independently exercised by the local execution fixture and readiness probe.
- Planning skill validation and `git diff --check`.

[Machine-readable validation](evidence/targeted-planning-revisions-2026-10-05/validation.json) separates deterministic tests from live results. GitHub's deterministic planner check passed on `5afe0fa6`; broader CI is incomplete because several jobs could not acquire hosted runners after repeated attempts. [Retained CI status](evidence/targeted-planning-revisions-2026-10-05/ci.json) records the affected checks. Local success does not establish those remote checks or live acceptance. PR #117 remains draft.

## Fresh Amazon planning checkpoint (before execution)

Frozen production/harness candidate `5afe0fa6`, cohort `targetedlive20261005a`, run `targetedlive20261005a-amazon-1`. Before dispatch, [deployment readiness](evidence/targeted-planning-revisions-2026-10-05/provider-readiness.json) resolved the exact configured model and retained the 96,000/32,768 request allowances. A zero-inference disposable Browser/Document probe verified local page reading, independent XLSX cells and Browser cleanup.

The [campaign ledger before dispatch](evidence/targeted-planning-revisions-2026-10-05/campaign-before.json) retains **EUR 84.319318 / 150**, including **EUR 2.606753** historical unknown reservations. No code-review run, cohort expansion or uncertain invocation replay is included. Generation and review precede explicit artifact approval and any execution.

The initial proposal compiled in two calls with zero repairs, but review rejected missing continuation handling and extraction absence placeholders. Two explicit global revisions were necessary to add observation-consumption scopes and replace technical interpretation/pass-through tasks with deterministic bindings; a field-only revision cannot authorize that restructuring. Revisions [2](evidence/targeted-planning-revisions-2026-10-05/rejected-r2.json), [5](evidence/targeted-planning-revisions-2026-10-05/rejected-r5.json) and [8](evidence/targeted-planning-revisions-2026-10-05/rejected-r8.json) remain retained. Static compilation did not override review findings.

The final correction used **eight explicit `EditablePaths`**: three manifest predicates, three page-status literals and two publication guards. The real provider returned only typed patches. Revision **10**, artifact `6a473e95d4e9e26f2e4b017499ac83b86cfcb91b3af0701309c93b41047bd59b`, validates and awaits explicit execution approval. The [complete comparison](evidence/targeted-planning-revisions-2026-10-05/targeted-live-review.json) confirms no changes outside those paths, unchanged accepted requirements and preserved writer/verification/cleanup. The targeted patch used **one planning call and no additional repair**.

| Measurement | Fresh planning | Execution |
| --- | --- | --- |
| Logical calls / physical attempts | 7 / 7 | Not started |
| Automatic repairs / explicit revisions | 2 / 3 | Not started |
| Discovery reads | 2 | Not started |
| Verified input / output tokens | 81,357 / 28,517 | Not measured |
| Cost | EUR 1.126647 | No dispatch |
| Cumulative planning latency | 339,625 ms | Not measured |
| Mapping samples, processed items, cache results, request sizes | Not applicable | Unverified |
| Independent execution oracle | Not applicable | Not run |

The campaign upper bound is **EUR 85.445965 / 150**, with the same historical unknown reservations and no new unknown usage. Planning required review iteration and did not meet a one-call target. [Exact artifact and requirement review](evidence/targeted-planning-revisions-2026-10-05/amazon-review.md) are ready for the user; approval acknowledgments have not been generated or submitted. The six-slot acceptance gate remains incomplete; this task authorizes only the fresh Amazon run.


## Inspect the retained run

Use the frozen source/harness `5afe0fa6` with the same deployment configuration, execution path and manifest hashes. The existing encrypted journal retains requests, responses, revisions, receipts and reservations. These read-only commands inspect the retained run and six-slot report:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --run targetedlive20261005a-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --cohort targetedlive20261005a
```

The [original artifact review](evidence/targeted-planning-revisions-2026-10-05/amazon-review.md) and [pre-execution cohort report](evidence/targeted-planning-revisions-2026-10-05/cohort.json) remain unchanged as historical checkpoints. The user subsequently approved the exact revision/hash and all five requirements. That approval has now been consumed by one completed execution; **never reset or replay this run**. See the execution result below and the [post-execution cohort report](evidence/targeted-planning-revisions-2026-10-05/cohort-after-execution.json).


## Approved execution: retained failure

The user explicitly approved the existing review with “I give you my approval.” The [submitted command](evidence/targeted-planning-revisions-2026-10-05/approval.json) binds reviewed revision 10, artifact `6a473e95d4e9e26f2e4b017499ac83b86cfcb91b3af0701309c93b41047bd59b` and all five requirement IDs. The approved session became revision 11 without changing its TaskPlan or YAML. The existing entry confirmation was answered `true` under that approval. Frozen production/harness `5afe0fa6` was executed once; no production source, limit, permission or oracle changed.

The initial Browser manifest contained **44 pages / 1,438 records**. Every cursor was read once before any further navigation. A read-only audit verified the exact cursor chain, snapshot identity, URL, per-page record counts and complete manifest/capture flags. All **44 independent mappings** completed, processing all **1,438 items**; **40 were cache hits**, four were misses, and one miss used its permitted repair. The reported sample count across cold invocations was 15; examples did not replace full execution. [Original mapping telemetry](evidence/targeted-planning-revisions-2026-10-05/mapping-telemetry.json) retains per-invocation counters, sizes and durations.

The next step, `decide_consent_from_compact_observations`, failed with **`LLM_BUDGET_EXCEEDED` before provider dispatch**. Its complete rendered prompt was **902,877 UTF-8 bytes**, estimated at **537,530 tokens**, against the unchanged 96,000 allowance. This was neither a provider-schema rejection nor the 30-minute deadline. No consent decision/click, post-consent capture, product navigation, product-field interpretation or document operation ran. The root Browser finalizer succeeded; the independent oracle confirmed closure and reported exactly `workflow_execution_failed` and `workbook_missing`. An independent filesystem check confirmed the approved destination did not exist.

| Measurement | Planning | Runtime execution |
| --- | --- | --- |
| Dispatched logical calls / physical attempts | 7 / 7 | 6 / 6 |
| Repairs | 2 automatic planning repairs | 1 mapping repair |
| Calls by runtime responsibility | — | 1 URL interpretation + 4 mapping generations + 1 mapping repair |
| Verified input / output tokens | 81,357 / 28,517 | 9,558 / 4,297 |
| Cost | EUR 1.126647 | EUR 0.157712 |
| Latency | 339,625 ms | 1,731,136 ms (28m 51s) |
| Unknown usage from this run | 0 | 0 |
| Independent execution oracle | — | Failed; cleanup passed |

One further global request was rejected locally before dispatch and is not included as a paid model attempt. Mapping-request telemetry reports 5,084–8,083 bytes for cold invocations; estimated complete mapping prompts reached at most 7,938 tokens. Those bounded samples did **not** establish that the assembled consumer request would fit.

Campaign known cost is **EUR 82.996923** plus **EUR 2.606753** unchanged historical unknown reservations: **EUR 85.603676 / 150**. There is no new unknown reservation, unresolved external invocation or `RUN_NEEDS_RECONCILIATION`. The durable run is `failed` with completed finalization. The six-slot cohort remains **0/6, incomplete**: one Amazon failure and five unexecuted slots. No code-review evaluation or cohort expansion occurred.

## What the failure establishes

Read-only inspection through the public encrypted run-store API confirms that the global request contains only its four declared business inputs. There is no raw-collection leakage from the compiler. The supposedly compact projection itself remains too broad: **1,223 selected records**, with **351,723 bytes of selectors**, **362,936 bytes of URLs**, **58,499 bytes of group strings**, and only **27,583 bytes of record text**. The selected-record collection accounts for 871,067 bytes of the 902,390-byte business-input object. Selecting fewer fields and calling the result “compact” did not make this consumer's request sufficiently small.

The next generic regression should reproduce this composition using arbitrary operation/field names and long observed references, then verify a **consumer-specific** projection: only the observations and fields needed for that decision, with exact bulky action references retained through existing bindings until needed. Preserve complete source coverage, order and provenance; do not truncate strings, guess missing values, automatically partition a global decision or enlarge allowances. Existing independent extraction, deterministic bindings and explicit review remain the mechanisms. This execution does not justify changing `mapping.dynamic` or introducing another planning layer.

A separate performance finding warrants a deterministic encrypted-persistence regression. The completed journal contains **739 invocations / 2,360 events** and serializes to **148,723,254 bytes**. `DataBefore` plus `DataAfter` account for **114,727,213 bytes**. A read-only post-run measurement took roughly one second to load it and 0.4 seconds to serialize it. The current cancellation monitor polls every 200 ms through a full run read; journal saves also read the retained state. These measurements and growing gaps between millisecond Browser calls support investigating repeated full-state work. They do not isolate its exact share of total latency. The run was not extended, and no persistence optimization was applied during execution. [Sanitized payload/journal measurements](evidence/targeted-planning-revisions-2026-10-05/journal-analysis.json) contain sizes and counters, not source content.

## Delivery status

No production or test source changed during this approved execution; the preceding 4,574-test, package, AOT and recovery results remain evidence for the frozen candidate. Remote CI on the documentation follow-up separately failed a Copilot cancellation fixture's temporary-directory teardown (`Directory not empty`); its single retry could not acquire a hosted runner. [Retained CI evidence](evidence/targeted-planning-revisions-2026-10-05/ci-after-execution.json) preserves both outcomes. This unrelated check remains unresolved and is not reported as passing.

PR #117 remains draft. All prior proposals, approvals, receipts, historical benchmarks and unknown reservations remain intact. The approved YAML/review files are unchanged, and this started execution cannot be revised or replayed. A future changed artifact requires a fresh identity and its own explicit approval.
