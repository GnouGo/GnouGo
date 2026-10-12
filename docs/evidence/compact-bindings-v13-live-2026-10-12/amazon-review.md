# Amazon artifact review — approved and executed once; acceptance failed

Frozen source: `1ffe75d5401ed2cdbe52fd0c2f7ff5ca9d8024c2` (`compact-bindings-v13`).
Run: `compactv1320261012a-amazon-1`, revision **8**.
Artifact hash: **`243a67abc3c44ccdb97ef6fda8e1a8f794ab88501f3685fa47a38103af6a3d16`**.
[YAML](workflow-r8.yaml), [TaskPlan](taskplan-r8.json),
[requirements](requirements-r8.json), [review checks](review-checks-r8.json).
This is a newly generated artifact. No old artifact approval is reused. The user subsequently acknowledged all four requirements and approved this exact revision/hash for one execution. See [approval command](approval-r8-command.json) and [authorization](execution-authorization.json).

## Explicitly acknowledged requirements

| ID | Reviewed work |
| --- | --- |
| `search_amazon_fr` | Open Amazon.fr, select the observed search control, fill the public query `chaussure geox homme 45` and submit. Browser validates the observed reference and fill compatibility. |
| `extract_up_to_10_products` | Select the first listed product IDs in offered order, with a real 1–10 selection contract, validate offered membership, reconnect original records and visit each selected URL sequentially. Preserve repeated selections. |
| `write_excel` | Write a real XLSX with `name`, `description`, `price`, `error` headers and one row per selected product. Observed fact IDs resolve through strict offered/original lookups. Missing facts remain null/blank. The output path comes from the actual writer result. |
| `cleanup_browser` | Root `finally` closes Browser on success or verified failure, subject to unchanged unknown-completion protection. |

Output: `workflows/schema-portability-20261002/compactv1320261012a-amazon-1/products.xlsx`, within the existing workspace.

## Composition review

The accepted requirements, six external operation declarations and arguments,
writer header binding, cleanup and public outputs are preserved. The plan adds no
unrequested action or selection criterion. It contains three independent page
extraction stages and three genuine decision stages; the product stages execute
per visited product. Every page is processed. Global decisions receive only compact
candidate views, not full snapshots. Adaptive mappings may specialize unresolved
items within the existing shared runtime/campaign budgets; there is no fixed promise
of one inference call per stage.

The two original-record views are now deterministic `foreach` page exports of
`item.records`, lowered by the existing compiler. They require no learned copying.
Selected IDs are checked against offered candidates, then resolved against those
original records. Nullable name/description/price branches copy original text
through strict lookups; the description is one entire selected block, without
rewriting. Structured row assembly is deterministic and preserves the supplied
header, null cells and order. Browser retains complete observations separately.

The model did not add the requested redundant `captureTruncated` and
`manifestTruncated` assertions. Existing success/non-null assertions remain.
Producer inspection confirms `observation_complete` rejects either truncated
condition with `OBSERVATION_INCOMPLETE` before returning a usable snapshot; this
review relies on that producer guarantee, without weakening it. Empty/unusable
acquisitions remain explicit producer failures.

Interpretation still chooses relevant blocks and reports observed errors. The
independent oracle must reject missing coverage, false absence, rewritten text,
unsupported errors or cross-product contamination. Compilation and this review do
not prove that the future model selections or live site will pass those checks.

## Measurements and accounting

Current artifact: **26 tasks, 9 workflows, 35 steps, 8 sets, 202,763 YAML bytes**.
Typed original copying and row formatting use zero inference. This is a new
composition, not a same-TaskPlan compiler benchmark.

Planning: **5 logical calls, 6 HTTP attempts, 0 automatic repairs, 3 explicit
revisions, 2 discovery reads**, 657,622 ms cumulative harness time.
Verified tokens: 86,904 input / 31,331 output. Known cost:
**EUR 1.2196734404117490460555506256**. One recorded HTTP 500 was retried within
the existing policy; every logical request has a committed receipt. No new unknown
attempt remains.

Campaign upper bound: **EUR 138.74391252313054071234972751 / 200**,
including unchanged unknown reservations of EUR 10.524647007054257881912280394.
Approximately EUR 61.26 remains; reservations are not released by archival.

## Execution outcome

All 29 applicable candidate CI checks passed before execution; four conditional
publishing checks were skipped. The exact revision/hash was executed once after
explicit acknowledgment of all four requirements. Approval persisted at session
revision 9 without changing the reviewed YAML.

Browser opened the homepage, submitted the search and returned a complete
54-page / 1,540-record snapshot. The generated mapping processed every page but
emitted no candidates. The subsequent `has_products` assertion failed with
`INPUT_VALIDATION` before product visits. **No product page was visited and no
XLSX was written.** Workflow cleanup succeeded and the independent oracle
confirmed the Browser was closed. Acceptance failed with
`workflow_execution_failed` and `workbook_missing`.

The [full result and zero-inference diagnosis](README.md#exact-offline-diagnosis)
preserve the original mapping, both faulty predicates, accounting and encrypted
receipts. No approved YAML was patched, no execution was retried and no historical
invocation was resumed. Runtime cost was EUR 0.2470627384861123435974798119;
final campaign upper bound is EUR 138.99097526161665305594720733 / 200, including
unchanged historical reservations. PR #117 stays draft.
