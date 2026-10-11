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

### First fresh attempt

Candidate `2bccddfc`, cohort `consumerbindings20261009a`, run
`consumerbindings20261009a-amazon-1`: [retained result](fresh-attempt-result.json).
The planner reserved one logical call and stopped before provider admission:
**zero physical attempts, zero new tokens/cost, zero Browser actions, no XLSX**.
The prior `gluev420261008b-amazon-1:8:…` request has no completion receipt or
inconclusive closure. The existing campaign guard blocks fresh admission while
that record is unresolved. Its full reserved cost remains in the ledger.

This is not an execution success or provider-schema rejection. The stopped fresh
run is not replayed. Explicit authorization to retain the exhausted historical
run as inconclusive is pending; no historical record has been changed. The
existing archival checks are extended only to recognize the live run's journal
layout, with regressions for started executions, remaining attempts, successful
results, mismatched identities and committed receipts. This adds no production
planning or runtime behavior.

### Completed local gates

[Validation details](validation.json): all 54 local Browser/Document/recovery cases
pass across the original run and the two corrected assertion reruns. Both indexed
fixtures independently validate XLSX cells, visits and cleanup. Core, Planning,
Integrations and Persistence Release packages pass with `-warnaserror`; the
published macOS ARM64 Native AOT smoke passes, including encrypted recovery.
The full solution on production candidate `2bccddfc` passes **4,965 tests, 13
skipped, zero failures**, with `-warnaserror` and exit code 0. Its full host suite
completed. The subsequent harness-only change `cedb77e8` passes 56 campaign tests;
production compiler/runtime sources are identical. Remote CI remains running at
publication; no completed check has failed. PR #117 remains draft.

The previous `b9421a3f` planner-validation and host-test jobs were cancelled after
six hours. Their [GitHub annotations](prior-ci-timeouts.json) explicitly identify
the maximum execution time, rather than an assertion failure or superseding push.
The local orphan-listener reproduction blocks after its retired bounded queue
fills; the ownership fix removes that cause without dropping events or increasing
timeouts. This explains the reproduced local hang; it is not a captured remote
stack trace. New complete test runs remain the verification of the correction.

### Authorized archival and subsequent fresh attempt

The user subsequently authorized archival of the exact exhausted historical run.
Its inconclusive closure is now durable, with original evidence and reservations
unchanged. [Follow-up evidence](archival-and-fresh-live/README.md) records the clean
admission audit and fresh `consumerbindings20261009b-amazon-1` attempt on
`fb07a92c`. All 29 non-skipped CI checks passed. Planning stopped because its
producer allowed nullable product records while the accepted output did not;
Browser execution and XLSX creation did not start. The campaign upper bound is
EUR 116.276178030274, including all unknown reservations. PR #117 remains draft.
