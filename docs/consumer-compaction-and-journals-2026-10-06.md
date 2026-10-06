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

Frozen candidate **`e2179af7d761f1f5d083671d0b9b1073a2e2b8fe`** passes **4,589 solution tests**, zero failures and 12 existing skips across 33 projects, with `-warnaserror`. The skips are seven Windows-only cases and five separately authorized paid Copilot cases. The real browser review smoke is included by setting `PLAYWRIGHT_MODULE_PATH` to the packaged Playwright module. Affected Core/Planning/Persistence Release packages, 29 planning Native AOT checks, published CLI/server encrypted recovery and skill validation pass.

Development validation caught guidance exceeding a retained discovery allowance by 11 tokens; the final wording fixes it without changing the limit. A later regression also prevents metadata-only saves from publishing unspecified edits to historical human dialogs. Earlier solution runs overlapped edits and are retained as failed development runs; the frozen final run is the acceptance result. Publishing must pass **global `-p:PublishAot=true`**, as in the existing publish script, so persistence dependencies precompile EF queries. A local-project-only CLI publish failed the index oracle despite preserving receipts; the corrected frozen publish passes the same oracle. The server uses the existing publish profile with C# warnings as errors and only its two exact pinned EF experimental notices accepted; no suppressions were added.

[Measurements](evidence/consumer-compaction-and-journals-2026-10-06/deterministic-measurements.json), [solution validation](evidence/consumer-compaction-and-journals-2026-10-06/solution-validation.json) and [publish outcomes](evidence/consumer-compaction-and-journals-2026-10-06/package-checks.json) separate deterministic evidence from live acceptance.

## Live boundary

Only one fresh Amazon evaluation is authorized for this correction. The campaign's pre-dispatch upper bound remains EUR 85.603676 / 150, including EUR 2.606753 of retained unknown reservations. Generation must use new identities and the unchanged prompt/oracle, followed by revision/hash-bound human approval and explicit requirement acknowledgments. No code-review evaluation, historical invocation replay or cohort expansion is included. PR #117 remains draft; deterministic success does not establish live acceptance.


## Fresh Amazon planning and review

The unchanged harness/prompt/oracle ran from a clean isolated checkout of `e2179af7`, using new session/run `consumerjournal20261006a-amazon-1`. Real local Browser/Document readiness passed with zero inference. [Frozen cohort and accounting](evidence/consumer-compaction-and-journals-2026-10-06/cohort-awaiting-approval.json) record source/harness, configuration, corpus/oracle hashes and limits.

The first artifact compiled but still passed a broad links/selectors view to consent interpretation and lacked complete observation consumption. Revision 5 added manifest loops but sent the full raw collection to whole-value extraction. Revision 7 replaced that with an unnecessary interpretation call per record. All three proposals and their explicit implementation feedback are retained. One incorrectly named revision-command field was rejected by strict deserialization before any dispatch; the corrected existing `text` field was used. No source changes, limits, oracle changes or new planning mechanics were introduced during these reviews.

**Revision 9** now uses direct typed foreach exports for consumer views, with no per-record inference or dynamic mapping. Initial consent/blockage and visited-product interpretation receive text/context without selectors or unrelated links. Product selection retains the observed hrefs it needs for subsequent navigation. Original manifest/page completeness flags guard data consumption; every cursor is read before interaction/navigation. A fresh snapshot follows consent. Real product visits, the actual workbook-writing operation and root cleanup remain present. Nullable fields, nested ordering and complete source data remain intact.

The generated workflow is reviewable, but **has not been approved or executed**. [Exact revision/hash, requirements and execution review](evidence/consumer-compaction-and-journals-2026-10-06/amazon-review.md) are ready for explicit human acknowledgment. Necessary business views can still exceed runtime limits on actual pages; the unchanged guard will reject them. Successful compilation is not an execution-oracle result.

Planning totals: **6 logical calls / 8 physical attempts, 1 repair, 3 review revisions, 2 discovery reads**; **78,900 input / 27,657 output tokens**, **EUR 1.092654**, **919,271 ms**. All new usage is verified. Campaign upper bound: **EUR 86.696331 / 150**, including the unchanged **EUR 2.606753** historical unknown reservations. Runtime interpretation, mapping, product visits, XLSX values, cleanup and live journal measurements remain unverified. No code-review case, cohort expansion or uncertain invocation replay occurred; the six-slot report remains 0/6 and incomplete.

Read-only inspection, from the frozen checkout:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --workspace /path/to/workspace --run consumerjournal20261006a-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --workspace /path/to/workspace --cohort consumerjournal20261006a
```

Execution uses the same frozen build and `--schema-portability execute --case amazon --cohort consumerjournal20261006a --run consumerjournal20261006a-amazon-1 --review-command <explicit-approval.json>` with the same campaign/workspace options. The approval must bind revision 9, its exact artifact hash and all five explicitly acknowledged requirements. Never rerun this identity after execution starts.

GitHub deterministic planner validation, stable unit tests and Agent.Server tests pass on the frozen candidate. Other platform/package jobs may still be running in the [retained CI snapshot](evidence/consumer-compaction-and-journals-2026-10-06/ci.json). PR #117 remains draft.
