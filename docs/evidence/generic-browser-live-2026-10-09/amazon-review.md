# Amazon revision 6: concrete artifact review

- Run: `genericbrowser20261009a-amazon-1`.
- Frozen candidate: `1d94689655d08188fbd06c9b0696ddf681edb170`.
- Revision: **6**.
- Artifact hash: **`25115dd75531e0be89e5e95062436ca275ab145c2a11f97cc10a039d96228070`**.
- Input: `query = chaussure geox homme 45`; at most **10 products**.
- Output: `workflows/schema-portability-20261002/genericbrowser20261009a-amazon-1/products.xlsx` inside the configured workspace.
- [Exact YAML](amazon-r6.yaml), [TaskPlan](plan-r6.json), [accepted requirements](requirements.json), [review checks](review-checks.json).

## Accepted requirements beside actual work

| Requirement to acknowledge | Accepted work | Actual operations and bindings |
| --- | --- | --- |
| `searched_amazon_fr` | Search Amazon.fr from the single public `query` | `derive_search_url` derives the encoded search URL; actual Browser `open_amazon_search` acquires `observation_complete`. Runtime checks require Browser success, a snapshot, and false capture/manifest truncation. |
| `listed_first_products` | List at most the first ten products from observed results | `extract_search_page_views` processes every complete page into candidate identity plus observed text. Typed per-item export and one-level flatten feed `select_candidate_ids`. Its result contract limits IDs to ten. Lookup validates offered IDs, then reconnects exact original Browser records via their `reference`; the original order and repeated selections remain supported. |
| `extracted_product_details` | Visit each selected product and extract observed name, description, price and explicit missing/error information | The sequential product loop navigates using each original non-null `href`. Mandatory success/completeness checks precede independent extraction of compact product facts. `reconcile_product_row` receives only these facts and small navigation/error metadata. Non-null rows retain nullable missing fields. An incomplete acquisition fails before extraction and writing. |
| `saved_excel_file` | Save the real workbook at the fixed path | A bounded TSV-formatting transform consumes at most ten typed rows. Actual Document `save_products_xlsx` writes the file. The public `filePath` is the writer's declared result, not a literal claim of execution. |
| `browser_closed` | Close Browser on success or known failure | Explicit Browser `close_browser_always` is the root finalizer. Existing reconciliation protection still prohibits unsafe cleanup after unknown external completion. |

## Data and inference review

The generated plan contains no consent task or consent instruction. Its external operations are exactly the original search read, per-product read, actual workbook write and Browser closure. Requirements and operation identities/order remain unchanged from revision 2.

The search extraction contract no longer requires invented indices or reconstructed action records. The decision view contains only `candidateKey` copied from the observed reference and `observedText`. Original records, URLs, selectors and action metadata are retained separately through deterministic projection/flatten. Selected IDs are checked against offered candidates before lookup against those originals. No global interpretation receives a full snapshot.

Inference remains explicit: query URL construction, compact product-ID selection, compact per-product fact reconciliation, and TSV formatting. Two `mapping.dynamic` definitions use the existing adaptive profile and shared runtime/campaign budget. They may specialize unresolved shapes; all original items must validate before publication. No token, time, permission or cost ceiling was increased.

The YAML has **61 step definitions, including 39 sets**, and **95,169 bytes / 2,055 lines**. This run makes no compiler-reduction claim. Descriptions, typed checks and compiled predicates are retained unchanged. There is one actual business iteration definition; its limit is ten.

## Results so far and execution gate

Planning reached review with **zero current diagnostics**, **4 logical calls / 7 physical attempts**, **zero automatic repairs**, **2 explicit revisions** and **2 discovery reads**. Three HTTP 500 responses delayed the second revision; the fourth transport attempt completed. Verified usage is **58,124 input / 15,974 output tokens**, cost **EUR 0.6831484603780282190078977726**, cumulative planning time **1,140,375 ms**. No new unknown usage remains. Campaign upper bound is **EUR 120.79227528823835861988367014 / 150**, including unchanged historical reservations.

Configured pricing/currency readiness, actual local Browser/Document execution and independent XLSX-read/cleanup readiness passed. The frozen binary hashes still match. Source correction regressions passed earlier; current CI is tracked separately.

**Historical pre-execution review, subsequently approved and executed once.** The user explicitly acknowledged all five requirements and revision/hash on 2026-10-09; [approval command](approval.json). The execution failed on the initial Browser acquisition; see [execution evidence](execution-result.json). Compilation and review do not establish extraction accuracy, nonempty results, real-site availability or XLSX correctness. Missing data, unsupported selections, excessive request size, provider errors or denied operations remain explicit runtime failures or oracle failures. The unchanged oracle must independently verify actual product-page observations, workbook cells/path and Browser cleanup. Empty or unsupported rows cannot count as a successful E2E.

The submitted approval identifies revision 6, the exact artifact hash and all five requirement IDs. No earlier approval was reused. This run has now executed and must not be replayed. PR #117 remains draft.
