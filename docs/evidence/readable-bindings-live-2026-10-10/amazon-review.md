# Amazon artifact review — revision 7

Run: `readablebindings20261010a-amazon-1`  
Frozen candidate: `e18452151b12fc9e978afc6450a8ca495e57d5ef`  
Revision: **7**  
Approval artifact hash: **`c8677c56bdd5b5a6924efb70670f500569d933e9dda17fc6f32485796b2e0998`**

[YAML](amazon-r7.yaml) · [Actual TaskPlan](planning-r7-plan.json) ·
[Accepted requirements](planning-r7-requirements.json) ·
[Compiler review](planning-r7-review.json) · [Measurements](artifact-measurements.json).
The raw YAML SHA-256 is recorded separately from the approval fingerprint.

## Accepted requirements beside actual work

No requirement acknowledgment has been selected or submitted.

| Requirement ID | Accepted outcome | Actual tasks and dependencies |
|---|---|---|
| `search_amazon_fr` | Search Amazon.fr using the single public `query` input. | `build_search_url` performs a bounded interpretation of `query` into the search URL; `open_search` navigates to that URL and requests `observation_complete`. The retained input is `chaussure geox homme 45`. No page-specific notice action is present. |
| `list_first_products` | At most the first ten products, in source order, without extra filtering, deduplication or invented observations. | `extract_search_candidates_by_page` independently processes every complete page into `{key,label}[]`. Its assembled `candidateArrays` is an array of arrays. Each page permits up to the existing 200-record producer bound; global `select_candidate_ids` returns at most ten IDs. `resolve_selected_offers` checks offered keys first, followed by `resolve_selected_original_records` against original `reference` values. |
| `visit_each_product` | Visit every selected product page. | `project_search_original_records_by_page` is a pure typed projection of `item.records`, followed by one-level flatten; it uses no mapping inference. `visit_products` runs sequentially over the resolved original records (maximum ten). Its `open_product_page` uses the exact original `href`, with a mandatory non-null assertion before navigation. Selected repetitions retain their order. |
| `export_xlsx` | Write a real workbook containing observed name, description, price and explicit missing/error data. | Each visit acquires complete pages, then `extract_product_facts_by_page` emits only compact nullable facts. `assemble_product_record` receives their flattened facts, not the snapshot, and produces one non-null product with required nullable `name`, `description`, `price`, `error`. `assemble_workbook_tsv` formats only the collected products. `save_workbook` invokes actual `document_write`. `validate_public_outputs` asserts writer success and returns its declared `filePath`, plus the typed products. |
| `cleanup_browser` | Close Browser, including after verified failure. | Root `always.close_browser` invokes `browser_close`. Existing unknown-completion protection still prohibits unsafe cleanup/replay. |

Output: `workflows/schema-portability-20261002/readablebindings20261010a-amazon-1/products.xlsx`
inside the existing workspace. No artifact from an earlier run is reused.

## Data, conditions and inference

- Search interpretation receives only the input query. Selection receives only all offered `{key,label}` candidates. Product interpretation receives only flattened compact facts from that visited page. TSV formatting receives at most ten typed products. No global interpretation receives a raw snapshot.
- Original Browser observations remain separate. The learned decision-view extraction must copy opaque references verbatim; strict lookup still rejects shortened or unknown IDs. The action-record projection is deterministic and preserves full original records.
- Both acquisitions request `observation_complete`. Existing Browser acquisition rejects capture/manifest truncation or an unusable document before returning a usable snapshot. Page consumers assert actual producer success and a non-null snapshot. The plan does not repeat separate flag assertions; completeness is enforced by that producer contract and independently checked by the unchanged execution oracle.
- Product fields may be null; whole product records may not. Nulls do not authorize invented text, booleans, prices or fallback URLs. Nullable facts still require correct extraction; static contracts do not prove semantic completeness.
- Two adaptive independent mapping declarations are visible: search candidates, and per-product facts. They may need specialized/per-item inference under the unchanged shared runtime and campaign budget. Every original item and the complete output must validate before publication; no two-call guarantee is claimed for this profile.
- Four interpretation declarations remain: search-URL formatting, selecting IDs, choosing/assembling product facts, and bounded TSV formatting. The product interpretation executes per actual visit (up to ten); other interpretations execute once. Required provider requests must fit the unchanged 96,000-token allowance. Oversize, budget or producer failures remain failures.
- Static validation is clean. The artifact has **17 TaskPlan tasks, 38 compiled steps, 18 sets, three workflows**, 100,225 YAML bytes; zero `checkedMapping` occurrences and three necessary first-present `m.select` calls. These counts do not predict runtime loop/inference attempts.

## Execution approval and independent acceptance

This artifact has not executed. The user's authorization covers this fresh validation campaign; execution still requires this revision/hash and explicit acknowledgment of all five requirement IDs above. The issued artifact approval hash, not the raw YAML hash, identifies the reviewed artifact.

Planning used five logical calls / six transport attempts, one automatic repair,
two explicit revisions and two discovery reads: 60,871 verified input tokens,
19,543 output tokens, €0.7903496317330730322122637324, and 536,597 ms cumulative
active planning time. All five calls have completion receipts; no new unknown
reservation was introduced. Campaign upper bound:
**€128.78518504065649687527456556 / €150**, including unchanged historical reservations.

After approval, execute once on the frozen candidate. Existing independent oracles
must verify real navigation, selected product visits, complete observations,
observed workbook cell values, output location and Browser cleanup. Model-generated
missing-data text, a successful compiler result or workflow status alone cannot
establish success. Any failure is retained; no uncertain invocation is replayed.
Remote CI is reported separately and must be checked before execution. PR #117
remains draft; this single run cannot establish six-run acceptance.
