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

The focused gate passed **109 planning tests** and **12 host tests**, including the real workbook fixture and encrypted persistence. Skill validation passed. Final solution/package checks and fresh live evidence follow below after collection. No deterministic result is presented as live acceptance. PR #117 remains draft.
