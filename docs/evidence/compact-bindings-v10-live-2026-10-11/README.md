# Authorized v10 planning: compiled, but review blocked

One paid planning session was authorized on frozen candidate `5a8afbfc` for
`compactv1020261011a-amazon-1`, maximum ten products. All 708 frozen binary hashes
and configured readiness were rechecked. Production code, prompt, permissions,
limits and the independent oracle were unchanged. No historical invocation was
resumed and no workflow execution occurred.

## Planning and transport

Initial generation reached revision 2 with two logical calls, zero repairs and no
compiler diagnostics. Review found whole-snapshot extraction, potentially truncated
HTML product reads, no independent extraction/ID reconnection and inference used for
typed assembly. [The original artifact and review](review-r2.md) are retained.

One limited explicit structural revision used the existing mechanism in the same
session, preserving accepted requirements and cumulative allowances. It corrected
complete acquisitions and page-wise extraction, but retained important composition
mistakes. One automatic scoped repair changed only the writer's rows binding.
Revision 5 compiles without diagnostics, but [manual review blocks execution](amazon-review.md).

| Artifact | Tasks | Steps | Sets | YAML bytes | Review |
| --- | ---: | ---: | ---: | ---: | --- |
| Revision 2 | 10 | 18 | 3 | 37,986 | Blocked |
| Revision 5 | 18 | 46 | 24 | 120,555 | Blocked |

These are different generated compositions, not an equivalence benchmark or proof
of improved business execution. The same-TaskPlan v9/v10 measurements remain in the
[offline gates](../compact-bindings-v10-2026-10-11/README.md).

Final planning: **4 logical calls, 1 repair, 1 explicit revision, 2 discovery reads,
8 physical attempts**, cumulative active planning **1,432,375 ms** (23m52s).
All four logical requests have committed receipts. Their serialized issued sizes
are 20,620 / 88,518 / 109,074 / 24,485 bytes; these are journal-request bytes, not
provider token counts. Verified usage is **42,641 input and 15,753 output tokens**.

Three attempts returned HTTP 500 with `ESG120`, `Routing failed`, and `Backend not
available, no answer from backend.` Another attempt ended with a transport failure
and no HTTP response. The existing pinned retry policy admitted the automatic
retry; no manual replay, retry-policy change or allowance reset occurred. The
uncertain attempt and its conservative reservation remain retained. The session's
eight-attempt ceiling is exhausted; no further paid call was made.

## Accounting and execution

- Verified planning cost: **€0.608568**.
- Additional retained uncertain reservation: **€1.356305**.
- Campaign upper bound: **€134.677922 / €150**, approximately **€15.322078** remaining.
- Historical unknown reservations are unchanged; total unknown attempts are six,
  including the new transport uncertainty. The exact decimals remain in the
  encrypted ledger and retained raw campaign report.
- Workflow execution, product visits, runtime mappings and real XLSX writing:
  **not started**. No live execution-oracle pass is claimed.

The pure header/oracle witness uses zero provider calls, zero MCP calls and no
workflow execution. Adding the header makes the generic witness pass; it does not
clear the other review blockers. Local helper setup needed the existing SQLite
native library path and explicit C# argument typing; these disposable corrections
modified no production component or saved session.

This delivery contains evidence only. The prior full solution, AOT/recovery and CI
gates remain attached to their tested source; no production rebuild is implied.
PR #117 stays draft. No execution approval is requested for revision 5.
