# Amazon artifact review

Run: `conditionalentry20261008a-amazon-1`
Frozen candidate: `c1c3de014d1909e46638c38d1cda849c030e48f6`
Revision: **13**
Artifact: **`8f2d6c67cb2318e8c8bafe6124e1f5ac4eacfbbf1712ed5fdb76f0b3fb0bbc6e`**

[TaskPlan](task-plan-r13.json) · [compiled YAML](amazon-r13.yaml) · [compiler review/accounting](review-r13.json)

The artifact compiles with no diagnostics. The user explicitly acknowledged all six requirements and approved this exact revision/hash for **one execution**. [Approval command](approval-command-r13.json) preserves that submitted acknowledgment; it was not populated before the user response. Compilation and this review do not establish successful execution or factual correctness.

| Requirement to acknowledge | Actual operations and bindings |
| --- | --- |
| `search_amazon_fr` | Open Amazon.fr with complete acquisition. Observe before optionally activating a consent control; capture fresh references afterward. Fill the observed search field with the sole public input `query`. The search branch requires a nonnull reference, declared fill compatibility and **not CAPTCHA** before entry. The operation retains its mandatory CAPTCHA assertion. Browser independently validates the actual observed reference and requested action. |
| `collect_first_products` | Process all complete page records through typed projections. Add original indices deterministically; keep original action records separately. Global interpretation receives compact identity/text/type fields and selects at most ten original IDs; strict lookup reconnects original hrefs and records. No observation sampling or truncation of that decision is authorized. |
| `visit_each_product` | Sequentially visit selected records using the href recovered from the original observation. A missing href produces an explicit missing-URL record; it cannot satisfy the independent visit oracle. Each visited page receives a fresh complete acquisition. |
| `explicit_missing_data` | Preserve capture-truncation assertions, nullable product fields and explicit blocker/missing-data status. Independent extraction processes every complete product page; flatten its compact observed facts before interpretation. CAPTCHA, absence and failed or oversized extraction remain observable failures/limitations, never successful live acceptance. |
| `save_excel` | Assemble observed product rows, perform the real `document_write`, require its successful completion and export the producer's nonnullable `filePath`. Destination: `workflows/schema-portability-20261002/conditionalentry20261008a-amazon-1/products.xlsx`. The oracle independently opens the workbook and compares values with captured observations. |
| `close_browser` | `close_browser_always` remains explicit finalization. Ordinary verified failure runs cleanup; genuinely unknown external completion still blocks cleanup and replay under existing reconciliation rules. The oracle checks closure. |

## Bounded execution

The concrete input is `query: chaussure geox homme 45`. Product visits remain bounded to ten and sequential. Complete observation page limits, the 1,000-item dynamic-mapping ceiling, cumulative sandbox resources, 96,000-input-token admission, provider policy, permissions and the 30-minute execution/inference allowance are unchanged. Adaptive product-page extraction remains disclosed and shares the existing runtime/campaign budget. No business operation, context limit or permission is added by approval.

The only change from revision 11 is four pure typed-copy projection bounds, from 1,000 to the existing producer capture ceiling of 10,000. They compile to checked deterministic projections, not per-record workflow calls. The existing expression/memory/statement limits still apply. Page loops remain bounded to 100; business visits remain bounded to ten.

Generation used eight calls, zero automatic repairs and five explicit implementation revisions; its planning allowance is exhausted and has not been reset. Planning took 734.881 seconds and cost EUR 1.947728. Campaign upper bound before execution: **EUR 112.657987 / 150**, including unchanged EUR 5.203327 unknown reservations. There have been zero execution calls for this run.

The retained oracle still checks consent where needed, complete observations, actual visits, observed values, workbook location/content and cleanup. A successful compilation or truthful failure workbook is not an oracle pass. No code-review execution, publication or uncertain invocation replay is included.

## Result of the authorized attempt

Execution has completed once. The runtime returned success and closed the browser, but the **independent oracle failed**: the initial Browser snapshot contained zero records, no search/product visit occurred, and the workbook contains a status row only. See [execution evidence](execution-result.json), [observed entry](observed-entry.json) and [independent workbook inspection](xlsx-inspection.json). This approval is consumed; the run must not be replayed.
