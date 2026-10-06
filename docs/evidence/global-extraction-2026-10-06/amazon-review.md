# Historical review of the approved Amazon artifact

**Execution status:** approved by the user and executed once; failed on the first expired snapshot cursor, with verified cleanup. [Execution evidence](amazon-execution.md). The following records the pre-execution review. The frozen production and harness are `4d0337b589aada4406531923c7596895c8246253`; cohort `boundeddecision20261006a`. Prior approvals and failed invocations remain unchanged.

- Run: `boundeddecision20261006a-amazon-1`
- Revision: **7**
- Artifact hash: **`fccc98605562f6c42ec84d0ca66c3dc7e6f72a01ed116f5c02cb8285197950e5`**
- Input: `query = "chaussure geox homme 45"`; maximum ten products.
- File: `workflows/schema-portability-20261002/boundeddecision20261006a-amazon-1/products.xlsx` in the confined workspace.
- [Exact YAML](amazon-r7.yaml) · [Requirements, TaskPlan, validation and accounting](amazon-r7.json) · [Manifest](manifest.json).

## Requirements beside actual work

| Acknowledgment ID | Proposed operations and bindings |
| --- | --- |
| `search_amazon_fr` | `build_search_url` constructs the requested search URL. `open_search_snapshot_manifest` navigates through Browser. The conditional clicks only an observed authorized consent selector, then takes and consumes a fresh snapshot. |
| `collect_bounded_products` | All snapshot pages are read before global product selection. `interpret_initial_search_state` or its post-consent counterpart selects at most the first ten observed links in source order; the sequential product loop enforces `maxItems: 10`. |
| `visit_each_product` | Each selected URL feeds `open_product_snapshot_manifest` inside `visit_products`. All that product's snapshot pages are consumed and independently compacted before selecting its observed name, description and price. Search summaries cannot replace the product observations. |
| `explicit_missing_or_blocked_data` | Completeness guards read original capture/manifest flags. Search CAPTCHA/blockers stop publication through explicit guards. Product fields permit null and require truthful status for missing observations; the workbook displays missing fields explicitly. No synthetic placeholder product is allowed. |
| `save_excel` | `assemble_products` directly binds the collected visited products. Explicit `build_products_tsv` formatting feeds the real `write_products_xlsx` Document operation. `productsFile` comes from that operation's `filePath`. |
| `close_browser_always` | The root finalizer calls `browser_close`, including verified failure paths. Unknown-completion safeguards still prohibit unsafe cleanup. |

## Extraction and inference

Manifest requests use `maxRecords: 100`; all page cursors are consumed within existing `maxItems: 100`. `captureTruncated` and `manifestTruncated` must be false. Consumed pages retain capture-completeness guards. A page continuation is resolved by reading every manifest page before navigation. Oversized or incomplete capture stops explicitly; the workflow does not claim success from a partial observation.

Each page loop explicitly extracts over its complete `observation.records`. The result is `recordCandidates: array<array<object>>`: exactly one outer entry per source record, with zero or more observed candidates inside it. Empty candidates require checking that record's irrelevance. Ordering, duplicates and nesting remain; generation examples never replace processing every record.

The initial consent/product view retains observed control/link references needed for the combined decision. Post-consent product selection carries only `kind/group/text/href`; the product-field view carries only `kind/tag/group/text`, with the already selected URL supplied separately. Global interpretation receives those candidate arrays and narrow shared metadata, never the raw page collections. The original observations remain evidence.

Five explicit interpretation sites are visible: URL construction, initial decision, conditional post-consent decision, each visited product's field selection, and workbook formatting. Ordinary product assembly is deterministic. Three static dynamic-extraction sites execute within page loops; each mapping invocation has at most one generation plus one repair, with validated cache reuse and unchanged cumulative limits. Actual provider calls, cache hits, extraction correctness and request sizes remain unverified live until execution.

The artifact contains 106 compiled steps across 11 workflows, including 72 checked `set` steps, three dynamic-mapping sites and one finalizer. It is 114,245 bytes / 3,127 lines with contracts and descriptions. These are static counts, not execution counts or a reduction claim against the structurally different historical artifact.

## Review history and remaining risk

Revision 3 compiled but still fed whole pages to global extraction and left completeness to model interpretation. It was rejected during review without execution. Revision 5 added independent extraction and guards, but its object item schema could not express an empty per-record candidate array. Revision 7 corrects those result types, narrows consumer views and replaces the unnecessary assembly inference. Requirements and unrelated operations/guards remain unchanged from revision 5.

Planning used five calls/physical attempts, zero repairs, two discovery reads and two review revisions: 72,806 verified input tokens, 21,846 output tokens, EUR 0.904614 and 331,564 ms. This does not establish first-call reliability. The campaign upper bound is **EUR 92.810955 / 150**, including EUR 2.606753 unchanged historical unknown reservations.

The independent execution oracle remains unchanged: real product visits, complete matching observations, exact output location, independently inspected XLSX values/columns and Browser cleanup. A provider error, CAPTCHA, missing data, budget exhaustion or incorrect extraction remains a failure or explicit external blocker. Compilation and this review do not establish execution success.

Approval must explicitly acknowledge all six IDs above and bind revision 7 and the exact hash. It authorizes one execution only. No code-review evaluation, cohort expansion, historical replay or permission increase is included; no redundant startup approval is added.
