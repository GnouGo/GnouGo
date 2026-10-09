# Checked consumer bindings and typed generation

Base: `b9421a3f`. Historical Amazon proposals, approvals, receipts and unknown
reservations remain unchanged. This correction does not replay them.

## Implementation

- Fresh approval-fingerprinted `compact-bindings-v5` moves eligible consecutive
  pure computations into the immediate consumer's checked input expression.
  Ordered intermediate schemas and diagnostic locations remain effective before
  dispatch. A single dynamic subtree avoids reordering independent expressions;
  host-owned inputs remain literal. Shared values, presence/failure preservation,
  branches, scopes, guards, retries and handlers retain materialization.
- Optional `TaskType.minItems/maxItems` become authoritative array constraints.
  Fresh strict model schemas use nullable bounds; absent DTO fields remain omitted
  from storage. Historical issued schemas and v1–v4 lowering remain unchanged.
- Object field access diagnoses array misuse without rejecting actual object
  properties named `0` or `length`. Boolean assertions do not imply cardinality.
- Explicit targeted revisions may edit a transform's mode, independent-item
  declaration and result contract. Fresh private patch authority is version 9;
  version 8 authority remains verifiable. Automatic repair cannot make those
  semantic edits or change `requires`; impossible immutable requirements stop
  with `REVISION_REQUIRED` before unrelated repair dispatch.
- Test-created telemetry listeners now have disposable owners. Retired queues
  receive no later activities. Production telemetry and backpressure are unchanged.

No mapping/runtime architecture, executor, planning phase or allowance changed.

## Same-plan compilation measurements

[Read-only comparison](compilation-comparison.json), independently of any changed
business composition:

| Metric | v4 | v5 |
| --- | ---: | ---: |
| Steps | 51 | 42 |
| Sets | 25 | 16 |
| Workflows | 5 | 5 |
| YAML bytes | 79,506 | 59,469 |
| YAML lines | 1,666 | 1,077 |

The v3 output still exactly matches the stored historical YAML. The sixteen retained
sets have independent roles: two assertions with no dynamic consumer subtree;
three checks before switches; six branch outputs/projections observed through
physical child identities; two shared/independently ordered preparations before
operations; and three workflow exports. The two adjacent pairs are branch-result
identities, not eligible unconsumed copies. The inventory records each step.

A controlled successful Flow fixture reduces actual invocations from 3 to 2 and
logical reconstructed journal bytes from 8,953 to 6,848 (including cleanup and
durable receipt replay). This is logical serialization size, not encrypted physical
storage or a live journal measurement. Its YAML decreases from 2,047 to 1,542 bytes.

## Validation and reproduction

The planner suite passes **1,193 tests** with `-warnaserror`. The focused telemetry
and host suite passes **90 tests**. Managed planning smoke and skill validation pass.
Complete local execution and final solution/package/AOT results are recorded below
when finished. Compilation measurements alone do not establish execution success.

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~BrowserSnapshotReceiptTests|FullyQualifiedName~PlanningModelRecoveryTests|FullyQualifiedName~TelemetryOwnershipTests'
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability replay-compile --compare-bindings \
  --workspace "$GNOU_GO_WORKSPACE" --campaign schema-portability-20261002 \
  --run emptyacquire20261008a-amazon-1
```

## Fresh live gate

[Configured provider readiness](provider-readiness.json) passed with zero inference.
[Accounting before dispatch](accounting-before.json) retains EUR 114.626115913 of
the EUR 150 ceiling, including EUR 5.203327458 for four unknown attempts.
Only one fresh Amazon run, at most ten products, is authorized. It requires concrete
revision/hash-bound artifact review and requirement acknowledgments. No code-review
evaluation or historical replay is included. PR #117 remains draft.
