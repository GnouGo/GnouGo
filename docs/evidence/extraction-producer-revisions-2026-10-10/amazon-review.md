# Amazon artifact review — revision 8

This is a new, unexecuted workflow. Compilation has no diagnostics. Execution and
the independent live oracles have **not** run; no approval has been recorded.

- Run: `extractproducer20261010a-amazon-1`
- Frozen candidate: `96cbf2176494e2d7c37373a3d08a07a0a1606846`
- Revision: **8**
- Artifact: **`e6b81b19d8b3c4ccacad4a112ab5e5f4838fd58bdc84e78b4464b28b3e9d41ad`**
- Actual input: `query = "chaussure geox homme 45"`
- Product maximum: **10**, in observed source order, without additional ranking or deduplication.
- Output: `workflows/schema-portability-20261002/extractproducer20261010a-amazon-1/products.xlsx`, under the existing confined workspace.
- [Exact TaskPlan](amazon-plan-r8.json), [exact compiled YAML](amazon-r8.yaml),
  [planning/accounting](amazon-planning-r8.json), [compilation measurements](amazon-compilation-r8.json).

## Accepted requirements beside the actual plan

| Requirement to acknowledge | Actual tasks, dependencies and checks |
| --- | --- |
| `search_amazon_fr` | `openAmazonFr` acquires complete observations. `extractSearchControlsByPage` processes all pages into compact observed identity/text candidates. `chooseSearchInputIds` returns exactly one ID. Offered-candidate lookup, `exportValidatedSearchIds`, and original-record lookup precede `fillSelectedSearchControl`. Its one-item body calls `fillSearchQuery` with the original observed reference and `query`, with submission enabled. Browser retains action-compatibility checks. |
| `collect_products` | `readSearchResults` runs after search submission. `extractProductCandidatesByPage` processes complete pages; `flattenProductCandidates` preserves their order. `selectFirstProductIds` receives only identity/text candidates and returns at most ten IDs. A non-null selection error fails the local assertion. `lookupSelectedProductCandidates`, `exportValidatedProductIds` and `lookupSelectedProductOriginals` validate offered identities before resolving originals. Repetitions are preserved. |
| `extract_product_data` | `visitEachProduct` iterates the selected original records sequentially, maximum ten. `readProductPage` navigates to each original `href`, requiring it to be non-null. `extractProductFactsByPage` processes every complete page into compact name/description/price/error facts. `assembleProductRow` reconciles only those facts and observed errors. The loop exports a non-null product object; nullable missing fields remain permitted, and `url` is copied directly from the actual Browser output. |
| `save_workbook` | `formatProductsTsv` receives only the collected compact product rows. One explicit interpretation formats TSV while retaining order and values. `writeWorkbook` writes the fixed `.xlsx` destination. Public `xlsxPath` and `success` come from the writer's declared `filePath` and `success`; public `products` comes directly from the loop export. The independent workbook oracle remains required. |
| `cleanup_browser` | Root `always` contains `closeBrowser`. Existing failure handling and unknown-completion protections remain authoritative; an uncertain external invocation cannot authorize cleanup or replay. |

## Data adaptation and inference being approved

All three independent extraction declarations bind `pages` and use
`each.input = "pages"`. Each page produces one typed array; the complete assembled
output is an array of arrays followed by explicit one-level flattening. The
extraction assertions check producer success, non-null snapshot, then
`captureTruncated == false` and `manifestTruncated == false` in short-circuit order.
Empty candidate arrays represent examined pages, not fabricated completeness.

The two original-record projections use typed `foreach` scope exports. They do
not reconstruct records with inference. Lookup and direct bindings retain original
action arguments. Search and product-selection interpretations receive only
`candidateKey` and `observedText`; product reconciliation receives compact facts
and actual error fields. No global interpretation receives a whole snapshot.
Full observations remain available separately.

The plan contains three extraction stages and four interpretation stages (the
product stages repeat only within the bounded ten-product loop). Adaptive mapping
may specialize unresolved shapes, including per-item inference, under the existing
cumulative runtime and campaign budgets. It is not promised to finish in two
mapping calls. Every original item must validate before publication. Request size,
sandbox, permission and source-grounding checks remain unchanged.

No consent action, fabricated selector, sponsored-product filter or deduplication
task remains. The plan has 28 tasks; compiled YAML contains 91 steps, including
56 sets, across five workflows (142,616 bytes). This correction makes no size
reduction claim. The retained extra forwarding tasks are not a reason to change
compiler semantics or relax any check in this evaluation.

## Evidence and approval boundary

The implementation passed 5,080 solution tests (13 existing skips), all 29
non-skipped CI checks, package/AOT checks and eight local Browser/Document cases
with independent workbook and cleanup inspection. Those are deterministic/local
results, not proof of the Amazon outcome.

Live planning used five logical calls, six physical attempts, three explicit
revisions, zero automatic repairs and two discovery reads. Known usage is 68,724
input and 21,233 output tokens; cost is **€0.870184**. Cumulative active planning
time is 572,345 ms. Campaign upper bound is **€123.881383 / €150** with historical
unknown reservations retained. No new unknown attempt exists.

Approval must name this revision and artifact and explicitly acknowledge all five
requirement IDs above. It authorizes one execution only. No older approval is
reused and no acknowledgment is preselected or submitted automatically. The
independent live oracles will check actual visits, observed extraction, XLSX cells,
output location and Browser cleanup; failures remain failures.
