# Targeted planning revisions preserve business work

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

## Fresh Amazon run

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


## Reproduce the pending live review

Use the frozen source/harness `5afe0fa6` with the same deployment configuration, execution path and manifest hashes. The existing encrypted journal retains requests, responses, revisions, receipts and reservations. These read-only commands inspect the pending run and six-slot report:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --run targetedlive20261005a-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --cohort targetedlive20261005a
```

Only after explicit acknowledgment of the five requirements in the [artifact review](evidence/targeted-planning-revisions-2026-10-05/amazon-review.md), submit the existing `approve` command in a private `--review-command` file with revision 10 and the exact artifact hash. Use the benchmark's existing `execute` command once; never reset or replay an execution already started. Approval has not been recorded for this artifact. [The frozen cohort report](evidence/targeted-planning-revisions-2026-10-05/cohort.json) therefore remains 0/6 and incomplete, with execution statuses kept distinct from planning validation.
