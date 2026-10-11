# Approved Amazon execution: finalization allowance exhausted

The user approved all four requirements and one execution of `compactbindings20261006d-amazon-1`, revision **8**, artifact **`0e86683a295778310311f8acda2d3c88a9b771a50cfdca30e940193fe24aad9f`**. The existing approval command recorded revision 9. The artifact and its original review remain unchanged. Frozen production/harness: `71315a5a7621f46f5a8b4ff1f3ab58213a5f445f`; ten products maximum.

**The execution failed.** It ran once for 45,348 ms, performed no product visits or dynamic mapping, and produced no XLSX. The independent oracle reports `workflow_execution_failed`, `browser_not_closed_by_workflow` and `workbook_missing`. No invocation was replayed and no limit was changed.

## Observed progress and cause

- Both home snapshots were consumed completely: 2 pages / 125 records, then 7 pages / 276 records. No consent click occurred; conditional consent handling remains in the approved artifact.
- The observed search control submitted the public query. The search snapshot declared **53 pages / 1,515 records**, with neither capture nor manifest truncation. Only **17 pages / 480 records** were read before failure. The snapshot is incomplete and does not establish complete search coverage.
- The encrypted execution journal records **307 / 10,000 normal steps** and **50 / 50 finalization steps**. All 50 finalization invocations were compiler-generated `set` steps, not external cleanup.
- `TaskPlanCompiler.CompileScope` places checked output selections and structured export assembly in `Finally`. Each completed page scope used two finalization steps: 9 home pages plus 16 result pages consumed the full allowance. The seventeenth result page was read, but its export could not execute. The root Browser cleanup then encountered the same exhausted allowance.

This is a generic compilation/finalization problem. The next correction should keep normal-path export glue out of cleanup's allowance when it does not depend on finalization, while preserving exports that genuinely depend on cleanup, failure-path availability and all cumulative limits. It needs a deterministic many-iteration scope/export regression before another live attempt. No production correction was made during this execution.

The oracle checked Browser state **before** its teardown close and correctly recorded failed workflow cleanup. Its subsequent `browser_close` succeeded, but is test teardown, not workflow evidence. No newly uncertain external invocation remains. The journal's `FinalizationCompleted=true` means finalization processing ended; it does not mean cleanup succeeded.

## Accounting and journal inspection

| Stage | Calls / attempts | Input / output tokens | Cost EUR | Latency |
| --- | --- | --- | --- | --- |
| Earlier planning and revisions | 6 / 6 | 66,235 / 24,537 | 0.947098 | 288,310 ms |
| This execution | 2 / 2 | 46,735 / 395 | 0.217876 | 45,348 ms |

Both runtime calls have verified receipts. Their estimated prompt sizes were 49,366 and 17,029 tokens, below the unchanged 96,000 request ceiling. Dynamic mapping calls, repairs and cache observations: **zero / not reached**. Campaign upper bound is **EUR 90.771022 / 150**, including unchanged EUR 2.606753 historical unknown reservations.

Read-only reconstruction measures 72,921,343 logical JSON bytes, including 55,459,852 snapshot bytes. The authoritative checkpoint is 86,069 bytes and its 1,444 referenced payload records contain 3,195,152 JSON bytes. Reconstruction took 2,536 ms. These are active referenced JSON measurements, excluding superseded blocks and encryption/database overhead; reconstruction time is not checkpoint latency. All required journal records were readable and no persistence failure occurred.

Evidence: [approval command](amazon-approval.json), [execution and observation coverage](amazon-execution.json), [journal inspection](amazon-journal-inspection.json), [six-slot cohort report](cohort-d-after-execution.json), [ledger](ledger-after-execution.json). Complete observations, requests, receipts and events remain encrypted; public evidence omits raw website content, selectors and host paths.

Read-only reproduction uses the existing frozen harness: `--schema-portability inspect-run --run compactbindings20261006d-amazon-1 --campaign schema-portability-20261002 --workspace <workspace>`, and `--schema-portability report --cohort compactbindings20261006d --campaign schema-portability-20261002 --workspace <workspace>`. Do **not** run `execute` again for this identity.

The previously reported 4,603 passing deterministic tests and package/AOT checks remain evidence for the unchanged candidate. This turn ran the approved live and read-only inspections; it made no source changes or new full-suite run. **0/1 attempted execution oracles passed; five cohort slots remain unexecuted.** PR #117 stays draft. No code-review evaluation or cohort expansion occurred.
