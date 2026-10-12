# Compact bindings v13 evidence

Production correction: `f1d79d2f` (following checked controls `92c997b3`, checked terminal outputs `5b3bdd2f`, and lexical branches `62ff3d62`). Measurement-only test hooks and documentation do not change production lowering. No paid inference, live site, saved workflow, receipt, campaign ledger or reservation was changed.

## Actual compiler comparison

[Complete v12 YAML](before-v12.yaml) · [Complete v13 YAML](after-v13.yaml) · [Three before/after examples](examples/README.md) · [All 27 sets and the three retained responsibilities](retained-sets.md)

| Same TaskPlan | v12 | v13 |
|---|---:|---:|
| Business tasks | 24 | 24 |
| Workflows | 13 | 7 |
| Static steps | 61 | 24 |
| Sets | 27 | 3 |
| YAML bytes | 240,061 | 203,908 |
| Maximum YAML line length | 290 | 290 |
| JavaScript bytes in expressions | 12,965 | 21,232 |
| Nested program-string wrappers | 0 | 0 |
| First-present helper calls | 7 | 7 |
| Strict lookup calls | 8 | 8 |

All 113 original expression-contract origins and all 27 primary set origins remain validated. Metadata uses separate schemas and static lexical scopes. Small schemas use compact YAML formatting, without changing their assertions. The JavaScript byte count rises because removed child scopes now appear as lexical code with their original input/output checks and descriptive names. This change reduces workflow structure and checkpoint work; it does not claim every expression is shorter. Necessary checked objects and first-present envelopes remain visible.

The three retained sets share validated original-record views or check products/rows before the writer and cleanup. The two pure capture loops retain iteration bounds and cancellation points; their removal is deferred until equivalent enforcement can be demonstrated without another mechanism.

## Real local execution and workbook oracle

The same composition reaches review in one deterministic planning call with zero repairs, then executes actual Browser and Document MCP processes against a loopback-only fixture. Ten individual product visits, complete page processing, unchanged action references, supplied headers, exact cells, row order, nullable missing fields, output path and Browser closure are checked independently. Candidates outside mapping examples and repeated selections are covered.

The eight v13 variants pass: complete, repeated, missing, one, empty, unoffered, incomplete and denied. The business oracle intentionally rejects zero-product and unsupported-absence claims even though the deterministic fixture verifies correct header-only/null-cell assembly. It also rejects rewritten descriptions. Compilation or runtime success alone is never treated as live acceptance.

The complete v12/v13 comparison uses 23 deterministic inference-adapter calls, including 11 mappings, in both profiles. Typed copying and row assembly use zero inference. The complete global decision request remains 2,690 bytes. No provider is configured or called.

## Measured runtime costs

Single paired local runs; values are evidence, not portable performance thresholds. Allocation counts cover the host process during engine execution, excluding Browser/Document child-process heaps and later explicit journal inspection. Physical bytes include the disposable encrypted store/index and workspace, not just payload blocks.

| Ten-product execution | v12 | v13 |
|---|---:|---:|
| Invocations | 359 | 143 |
| Normal steps / cleanup steps | 358 / 1 | 142 / 1 |
| Allocated bytes | 7,241,746,312 | 2,748,577,608 |
| Engine elapsed milliseconds | 21,756 | 8,319 |
| Logical journal bytes | 53,928,722 | 22,405,686 |
| Physical store/workspace bytes | 30,119,784 | 13,408,102 |
| Lease checkpoint calls | 1,316 | 586 |
| Cumulative checkpoint milliseconds | 14,743.02 | 5,738.66 |
| Frozen snapshot captures | 718 | 286 |
| Cumulative capture milliseconds | 834.56 | 232.25 |
| Snapshot restore calls | 359 | 143 |
| Cumulative restore milliseconds | 60.14 | 8.15 |
| Completed encrypted restart milliseconds | 1,870.53 | 877.79 |
| Completed restart allocated bytes | 159,054,368 | 79,036,128 |
| Repeated inference/external calls on restart | 0 / 0 | 0 / 0 |
| Lightweight check events | 833 | 1,089 |
| Reference-only check event bytes | 288,147 | 444,684 |

Checkpoint timing is measured around the existing public lease save; it includes its durable work, not individual private block writes. The measurement wrapper delegates capture/restore hooks exactly. Trace volume rises because former child input/final-output validations now have explicit check events. Events contain check names, origins, references, pointers, order and outcomes, never values or schemas. These bytes measure event attributes, not backend transport overhead.

A separate lexical probe returns exact observed text with 61 statements, 5,144 charged materialized bytes and 396 output bytes for both 1-byte and 1.2 MB unrelated observations. The unchanged memory ceiling is 50,000,000 bytes. Warm measured allocation is 67,928 bytes for the large unrelated payload. First-process/JIT costs are retained in the raw measurements; payload size does not change charged traversal/materialization.

[Raw compilation metrics](compilation-metrics.json) · [Runtime measurements](measurements/) · [Lexical probe](measurements/lexical-probe.cs.txt)

## Validation and limitations

Gate evidence records the intermediate corrections, including the rejected metadata-losing implementation and the nullable-capture regression. Original failing evidence was retained locally. The final origin audit and local oracles ran after both corrections.

- Complete focused Core: 1,350 passed, then two additional failed-child recovery cases passed.
- Complete Planning: 1,308 passed; all 67 fusion cases passed again after terminal lexical output support.
- Real local Browser/Document: 12 v13/equivalence cases passed; the final instrumented comparison and origin audit passed three more cases.
- Five Flow Release packages, Planning Native AOT (including new control/lexical metadata), and native encrypted CLI/server recovery passed on the final production source.
- Planning skill validation passed.

The whole solution is run with `-m:1 -warnaserror`. Its final result and exact-head CI job/step findings are recorded in the delivery comment on [PR #117](https://github.com/GnouGo/GnouGo/pull/117); no in-progress or skipped job counts as a pass. The earlier full run was stopped after the terminal lexical correction so the final source could be validated from the start.

The supported CLI/server publish profile emits exactly the two previously audited EF Core Tasks experimental notices. An initial invocation incorrectly promoted those notices with `-warnaserror`; that failure remains in `/tmp/gnougo-v13-cli-publish.log`. The supported profile was then used without adding suppressions or changing dependencies. Package builds and Planning AOT remain warning-as-error.

This evidence is offline. PR #117 stays draft; no new live result or financial authorization is implied.
