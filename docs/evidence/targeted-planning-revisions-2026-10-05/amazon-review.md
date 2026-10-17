# Amazon workflow awaiting execution approval

Run: `targetedlive20261005a-amazon-1` · candidate: `5afe0fa6` · revision: **10**

Artifact: `6a473e95d4e9e26f2e4b017499ac83b86cfcb91b3af0701309c93b41047bd59b`

[Exact TaskPlan and accepted requirements](amazon-r10.taskplan.json) · [Generated YAML](amazon-r10.yaml) · [Targeted revision comparison](targeted-live-review.json).

This is a reviewable generated artifact, not an executed workflow. All five requirements below await your explicit acknowledgment; none is automatically selected or approved.

| Accepted requirement | Actual planned work |
| --- | --- |
| `search_amazon_fr` | Use the single public `query` input to construct the search URL, open Amazon.fr, capture its observation manifest and read every frozen page. The evaluation supplies the original query, `chaussure geox homme 45`. Compact observed records support consent/blocker decisions; an observed consent selector is clicked conditionally, followed by a fresh capture. |
| `limit_first_three` | Interpret only compact business observations, select at most the first three product links in source order, and run a sequential product loop bounded to three items. |
| `extract_product_fields` | Actually navigate to each selected product. Consume every page of its own snapshot, compact individual records and interpret the compact observations for name, description and price. Original producer flags guard completeness; missing facts and blockers remain explicit. |
| `save_xlsx` | Build TSV from the collected product details, call the real `document_write` operation in `write_products_xlsx`, then call `document_read` in `verify_saved_workbook`. Returned paths come from the writer. Destination: `workflows/schema-portability-20261002/targetedlive20261005a-amazon-1/products.xlsx`. |
| `cleanup_browser` | Execute `browser_close` in the root finalizer `close_browser_always`, including after verified failures. Unknown external completion still prohibits cleanup/replay. |

## Data flow and limits

Each complete manifest is consumed before a click or navigation can invalidate its cursors. Manifest checks use actual `success`, `captureTruncated`, `manifestTruncated` and top-level `truncated` values. Intermediate page continuation is permitted because all frozen page descriptors are consumed; truncated captures and incomplete manifests stop the workflow.

Three independent extraction bindings compact initial, search and product records. Each source record produces an ordered array of selected observed records; nesting is retained, without an automatic global partition or summary. Page context is assembled once per page through deterministic value bindings. Global interpretation receives compact exports and explicit business context, never the raw observation collection.

Runtime inference remains explicit: URL construction, consent/product selection, product-field interpretation and TSV synthesis use interpretation; independent extraction uses the existing bounded mapping runtime with at most one generation and one repair per invocation. Cache reuse is revalidated. Actual request sizes, extraction quality, website availability and the resulting workbook remain unverified until execution. Existing ceilings, permissions, source grounding and the EUR 150 cumulative campaign gate remain in force.

The final targeted revision changed **only eight authorized values/guards**. The complete TaskPlan outside those paths is unchanged, including the writer, verification, task order, scopes, objectives, loops and cleanup. All 48 combinations for the three four-flag manifest predicates were checked during review. These checks establish those concrete conditions, not general proof of business completeness or successful execution.

## Evidence so far

Planning: **7 calls, 7 physical attempts, 2 automatic repairs, 2 discovery reads**, and three explicit review revisions (two structural revisions, then one targeted patch). All usage is verified: **81,357 input / 28,517 output tokens**, **EUR 1.126647**, **339.625 seconds** cumulative planning time. The targeted patch itself used one planning call and no additional automatic repair.

Campaign upper bound: **EUR 85.445965 / 150**, including unchanged **EUR 2.606753** historical unknown reservations. Execution has not started; no runtime tokens, sample counts, cache results, product visits or XLSX oracle pass are claimed.

After approval, execute this artifact once and apply the unchanged independent oracle to observed product visits/values, XLSX cells, destination and Browser cleanup. Earlier rejected revisions remain retained. No code-review run, cohort expansion, uncertain invocation replay or GitHub review publication is included.
