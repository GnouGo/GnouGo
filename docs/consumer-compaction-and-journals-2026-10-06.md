# Consumer views and durable journal checkpoints

This correction continues PR #117 from `4bb64d12`. It preserves TaskPlan, compilation, `mapping.dynamic`, permissions, request limits and execution oracles.

## Retained failure

The [previous Amazon execution](targeted-planning-revisions-2026-10-05.md#fresh-amazon-planning-checkpoint-before-execution) consumed all 44 observation pages but passed a 902,877-byte superset of compact fields to a global decision. Most bytes were action references unnecessary for that decision. Request admission correctly stopped before inference. Its logical journal reached 148,723,254 bytes, including 114,727,213 bytes of repeated execution snapshots. That run is retained and is not replayed.

## Consumer-specific views

Generation guidance now asks for a view per consumer, using existing field bindings, loops and independent extraction. Exact action references remain in source bindings and are passed only where needed. Global comparison and synthesis stay global; the host does not infer which facts may be deleted or truncate observations. Review displays nested result fields and scope export selections alongside named transform inputs.

The paired deterministic fixture uses arbitrary operation/field names and 120 records with long references. Both corrected variants process every record, including relevant observations outside mapping-generation examples, and retain all exact later action arguments:

| Variant | Broad request bytes / estimated tokens | Consumer request bytes / estimated tokens | Mapping inference |
| --- | ---: | ---: | ---: |
| Typed fields | 98,907 / 30,721 | 9,857 / 1,838 | 0 |
| Independent extraction | 98,914 / 30,724 | 9,864 / 1,840 | 1 |

These are complete serialized request measurements on a controlled fixture, not predictions for Amazon. The necessary-view-too-large, missing-observation and incomplete-snapshot tests still reject the request. The retained discovery-budget fixture remains within its unchanged 24,000-token allowance at 23,998 estimated tokens.

## Encrypted storage

New runs use private split layout 1 inside `EncryptedWorkflowRunStore`. Immutable tenant/run-owned blocks share large snapshot subtrees and receipt payloads. Invocation records and chunked events are published through a small authoritative checkpoint only after all referenced blocks are durable. Owner saves checkpoint changed invocation identities; ordinary saves and custom stores retain full-save semantics. Cancellation polling reads only the checkpoint. Concurrent human answers and cancellation merge under the existing write lock.

Public inspection and recovery still reconstruct schema 9, including exact numeric tokens, absent properties and explicit nulls. Full inspection can still be large. Existing monolithic runs are neither migrated nor rewritten. Older binaries reject the new layout, so hosts sharing ownership of new runs must be upgraded together. Missing, corrupt or cross-owner references fail closed. Orphan blocks after interrupted writes remain non-authoritative; no receipt is pruned and no uncertain operation is replayed.

Regressions inject failures before block writes, before checkpoint publication and after a committed receipt. Real Flow restart tests verify one external execution, cleanup only after verified completion, and `RUN_NEEDS_RECONCILIATION` when no completion was committed. Tests also cover concurrent answers across event-chunk boundaries, stale commands, index loss, tenant isolation and historical full saves. Local Browser/Document execution retains independent XLSX, visit and cleanup assertions while using the split encrypted store for compact-observation variants.

The growing-collection fixture checkpoints 70 invocations and preserves budget/accounting values. Its reconstructed journal is **85,531,547 bytes**; the 386 immutable block records contain **409,797 bytes**, and all writes including successive checkpoints total **636,253 bytes**. The largest checkpoint is **6,158 bytes**. No unchanged block is rewritten, reconstruction loads each referenced block once, and ten cancellation polls read zero blocks. On this machine, checkpoint median/p95 were **21.09 / 27.76 ms**. Stored/written bytes here measure JSON supplied to the encrypted record API, excluding encryption/SQLite overhead; they do not measure the database file or eliminate the large logical inspection result.

## Reproduction

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~CompactObservationTests|FullyQualifiedName~RecordedTargetedDiscoveryTests'
dotnet test tests/GnOuGo.Flow.Persistence.Tests -m:1 -warnaserror --logger 'console;verbosity=detailed'
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror -p:SkipModelMetadataGeneration=true
```

`SkipModelMetadataGeneration` avoids refreshing the generated deployment catalog during local validation; it does not skip tests or request validation. Published CLI/server recovery is checked using `scripts/verify-flow-v9-published.py`; the planning Native AOT executable exercises its existing smoke assertions.

## Live boundary

Only one fresh Amazon evaluation is authorized for this correction. The campaign's pre-dispatch upper bound remains EUR 85.603676 / 150, including EUR 2.606753 of retained unknown reservations. Generation must use new identities and the unchanged prompt/oracle, followed by revision/hash-bound human approval and explicit requirement acknowledgments. No code-review evaluation, historical invocation replay or cohort expansion is included. PR #117 remains draft; deterministic success does not establish live acceptance.
