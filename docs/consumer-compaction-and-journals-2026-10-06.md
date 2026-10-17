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

The user explicitly approved revision 9, its exact artifact hash and all five requirements. [Exact revision/hash, requirements and execution review](evidence/consumer-compaction-and-journals-2026-10-06/amazon-review.md) and the [submitted approval command](evidence/consumer-compaction-and-journals-2026-10-06/approved-r9-command.json) are retained. Approval advanced the session revision to 10 without changing YAML. Necessary business views can still exceed runtime limits on actual pages; successful compilation is not an execution-oracle result.

Planning totals: **6 logical calls / 8 physical attempts, 1 repair, 3 review revisions, 2 discovery reads**; **78,900 input / 27,657 output tokens**, **EUR 1.092654**, **919,271 ms**. All new usage is verified. Campaign upper bound: **EUR 86.696331 / 150**, including the unchanged **EUR 2.606753** historical unknown reservations. No code-review case, cohort expansion or uncertain invocation replay occurred.

## Execution outcome and narrow harness correction

The single approved attempt **failed before the business workflow ran**. The execution terminal handle was lost across the assistant interruption; the generated confirmation remained unanswered until after the unchanged 30-minute execution deadline. Recovering the same terminal and submitting the authorized answer then yielded `CANCELLED`. Elapsed harness time was **6,181,247 ms**, including the blocked console wait; it is not Browser execution latency. This is an orchestration/harness failure, not evidence of an Amazon, compaction or journal workload defect.

Runtime inference: **zero logical calls, zero physical attempts, zero tokens and EUR 0**. No workflow Browser/Document call, product visit, workbook write or workflow cleanup ran. The unchanged oracle failed with `workflow_execution_failed` and `workbook_missing`. Its two post-run Browser probes verified there was no active page and closed the empty browser session; they do not establish workflow cleanup. [Exact execution accounting](evidence/consumer-compaction-and-journals-2026-10-06/execution-result.json), [oracle](evidence/consumer-compaction-and-journals-2026-10-06/execution-oracle.json), [observations](evidence/consumer-compaction-and-journals-2026-10-06/execution-observations.json) and the [final six-slot report](evidence/consumer-compaction-and-journals-2026-10-06/cohort-final.json) retain the failed attempt. The other five slots remain unexecuted: **0/6, incomplete**.

A deterministic regression reproduced the secondary harness defect: `Console.In` uses a synchronized reader whose async method can block synchronously, preventing cancellation from reaching the pending wait. The benchmark now performs that read on a worker, bounds the wait with the original cancellation token and rejects late answers. No runtime, mapping, production contract, approval, oracle or limit changed. The regression fails on the original code and passes after the correction; **49 affected campaign/approval tests pass with `-warnaserror`**. This harness correction was made after the frozen live attempt, with **no further paid dispatch or replay**. The 4,589-test solution and package/AOT results above remain evidence for the unchanged production candidate, not a claim of a second full-solution run after the harness-only change.

Public KeyVault APIs reconstruct the cancelled journal as schema 9 with one completed human-input invocation and five events. It has **132,746 logical JSON bytes**, a **429-byte checkpoint**, and **16 retained immutable blocks / 144,900 block JSON bytes**, including superseded headers. These exclude encryption/SQLite overhead and are not representative collection-workload measurements. [Read-only measurement](evidence/consumer-compaction-and-journals-2026-10-06/cancelled-run-journal.json). Consumer request sizes, product coverage, XLSX values and workload checkpoint costs remain unverified live.

The approved YAML and all historical receipts/reservations remain unchanged. This started identity must not be reset, revised or replayed. A later evaluation needs a fresh identity, frozen harness/candidate and separately reviewed approval; this correction does not authorize a second execution. PR #117 remains draft.

Read-only inspection, from the frozen checkout:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect-run --campaign schema-portability-20261002 --workspace /path/to/workspace --run consumerjournal20261006a-amazon-1
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability report --campaign schema-portability-20261002 --workspace /path/to/workspace --cohort consumerjournal20261006a
```

The retained execution used the same frozen build and the exact revision-9 approval command with the same campaign/workspace options. **Do not issue `execute` again for this identity.** Keep the terminal attached when executing future, separately approved artifacts and answer their explicit runtime confirmation within the existing deadline.

GitHub deterministic planner validation, stable unit tests and Agent.Server tests pass on the frozen candidate. All non-skipped checks also pass on the documentation head before the harness fix: [retained CI snapshot](evidence/consumer-compaction-and-journals-2026-10-06/ci-before-harness-fix.json). New CI is separate from those completed checks. PR #117 remains draft.
