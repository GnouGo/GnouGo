# Approved Amazon execution: first snapshot cursor rejected

The user approved revision **7**, artifact `fccc98605562f6c42ec84d0ca66c3dc7e6f72a01ed116f5c02cb8285197950e5`, in response to the six-requirement execution request. Run `boundeddecision20261006a-amazon-1` executed **once**, on frozen source/harness `4d0337b589aada4406531923c7596895c8246253`. Its YAML is unchanged. Approval advanced session revision to 8. No old or uncertain invocation was resumed. [Review](amazon-review.md) · [Approval command](amazon-approval-r7.json).

**The execution oracle failed.** The first Browser call returned HTTP 202 and a complete-capture manifest containing three pages / 79 records. The following read supplied exactly its first issued cursor, with no intervening workflow navigation or interaction. Browser returned `INVALID_INPUT`: “The observation cursor is invalid or expired.” The workflow stopped before any page content, dynamic extraction, consent click, product visit or workbook write.

The failed MCP read has a durable verified completion. Cleanup then completed with **one finalization step**, and the independent oracle confirmed no active page before its own safety close. Normal execution used **18/10,000** steps and finalization **1/50**. This is a known completed failure, with no `RUN_NEEDS_RECONCILIATION` or duplicate execution. The unchanged oracle findings are `workflow_execution_failed` and `workbook_missing`. [Execution and accounting](amazon-execution.json) · [Observation sequence](amazon-observations.json) · [Journal receipts and counters](amazon-journal-measurements.json).

## Diagnosis and deterministic check

The exact returned cursor matches the request. The expired-cursor branch is consistent with the snapshot having been invalidated before the first read. The producer invalidates snapshots on main-frame navigation, including navigation initiated by page scripts. The live evidence does **not** include the triggering navigation event; HTTP 202 alone does not establish a CAPTCHA or a particular redirect. The precise live navigation cause therefore remains unverified.

The existing local `BrowserObservationTests` reproduce page-initiated navigation invalidating both legacy and paged cursors, then successfully capture the new document. All **three tests pass**, with warnings as errors and no paid inference. They also retain bounded content, coverage, idempotence and stale-cursor rejection. This confirms the intended safety mechanism, not a demonstrated defect in the cursor parser. No production code, contract, mapping behavior or test oracle was changed. [Validation](cursor-regression-validation.json).

The next generic investigation is bounded snapshot acquisition/recovery for page transitions: distinguish expired snapshots explicitly, retain the invalidation cause, and restart a read-only acquisition from a fresh snapshot when authorized. Any partial old snapshot must be discarded rather than combined with the new one; bounds and navigation/permission checks must remain. This is a proposed next correction, **not implemented or exercised by this run**. Accepting stale cursors or adding a fixed site-specific delay would weaken the current guarantee.

## Measured outcome

| Measure | Result |
| --- | ---: |
| Planning calls / physical attempts | 5 / 5 |
| Discovery reads / automatic repairs / review revisions | 2 / 0 / 2 |
| Verified planning input / output tokens | 72,806 / 21,846 |
| Planning cost / latency | EUR 0.904614 / 331,564 ms |
| Execution provider calls / physical attempts | 1 / 1 |
| Verified execution input / output tokens | 116 / 96 |
| Execution cost | EUR 0.003070 |
| Execution including oracle / total latency | 17,167 / 348,731 ms |
| Dynamic mapping calls / repairs / cache activity | 0 / 0 / not reached |
| Snapshot pages successfully read | 0 / 3 |
| Product visits / XLSX produced | 0 / no |
| Workflow cleanup | verified |

The sole execution inference constructed the search URL. All current-run provider usage is verified. Campaign upper bound is **EUR 92.814025 / 150**, including **EUR 2.606753** unchanged historical unknown reservations. No code-review evaluation or cohort expansion occurred. Current cohort: **0/6**, one failed and five unexecuted slots; failures remain in the denominator. [Cohort](cohort-after-execution.json) · [Ledger](ledger-after-execution.json).

Read-only journal inspection reconstructed 842,083 logical JSON bytes from a 3,079-byte checkpoint and 58 referenced blocks totaling 421,240 JSON bytes. These referenced-block figures exclude superseded blocks and encryption/database overhead. Checkpoint read was 253 ms; reconstruction 178 ms. They are inspection timings, not checkpoint-write latency or evidence of large-run performance.

The prior **4,640 passing solution tests** remain the source-validation baseline; the three relevant Browser regressions were rerun after this failure. CI was still running on the pre-execution evidence commit, with no failed checks in the retained snapshot. PR #117 remains draft. Deterministic composition success and earlier producer probes do not establish this candidate's live extraction or XLSX acceptance.
