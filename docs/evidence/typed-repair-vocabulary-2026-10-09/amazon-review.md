# Amazon revision 11: ready for explicit artifact review

- Frozen candidate: `99cc3dcaa56755be2de7de7cafb6ffd32b7a5e11`.
- Run: `repairvocabulary20261009a-amazon-1`; revision **11**.
- Artifact hash: **`a9fc1508a7c623cfb3140ed846c7755e2538c537921e931c18df0a0d32b5d058`**.
- Input: `query = chaussure geox homme 45`.
- Maximum: the first **10 products**, without sponsored filtering or deduplication.
- No cookie acceptance, refusal or customization. A genuinely blocking page must fail; a cookie-information link is not a blocker.
- Output: `workflows/schema-portability-20261002/repairvocabulary20261009a-amazon-1/products.xlsx` within the existing configured workspace.
- [Exact YAML](amazon-r11.yaml), [TaskPlan](live-revision-11-plan.json), [accepted requirements](live-accepted-requirements.json).

## Requirements beside actual work

| Reviewed requirement ID | Accepted result | Actual steps and dependencies |
| --- | --- | --- |
| `success` | Search from `query`, visit at most ten products, write observed name, description and price to the fixed workbook | `build_search_url` → `read_search_results` → independent page extraction → per-page typed projections and flatten → `select_search_product_ids` → lookup offered candidates → project selected IDs → lookup original records → `scrape_products` → bounded TSV formatting → actual Document `write_workbook`. `filePath` comes from the writer result. |
| `blocked` | Report an observed obstacle and do not declare success | Browser uses `observation_complete`; explicit success/capture checks precede page extraction. Global decisions receive compact evidence. Search and per-product `requires` stop work on reported blockage; original product `href` must be non-null before navigation. Producer limits and errors remain authoritative. |
| `cleanup` | Close Browser after successful writing, or during failure handling when completion is known | Explicit `close_browser` in root `always`. Unknown external completion still blocks unsafe cleanup or replay. |

## Adaptation and inference disclosed

Search extraction receives every bounded complete page through `extract.each`. Its candidate contract contains only `candidateId` and `nameText`, plus a separate page-level blocking-evidence array. The ID is copied from the observed producer reference. Typed item projections precede one-level flattening. The global selection receives neither raw pages nor action URLs/selectors/actions. A first lookup validates offered IDs; a second lookup resolves the original records. Selected order and repeated IDs remain unchanged; only the original `href` is used for product navigation.

For each selected product, Browser makes an actual complete acquisition. Independent extraction emits typed name/description/price facts and blocking evidence. Interpretation receives those compact views, the original observed search label and URL. It creates one non-null row with required strings. Missing observations may be presented as `ABSENT` outside source-grounded extraction; they must not be reported as observed facts. The independent workbook oracle remains unchanged and may reject incomplete or unsupported values.

The remaining interpretation work is explicit: query URL construction; compact candidate selection; per-product reconciliation/blockage assessment; final TSV formatting over at most ten rows. Two mapping step definitions use the existing adaptive profile, including possible specialization under the shared runtime/campaign limits. They are not promised to need only two inference calls. No allowance is increased; unknown reservations remain retained.

## Validation and limitations

- Compilation, contracts and public-output validation pass with no current diagnostics.
- Accepted requirements are byte-equivalent as JSON values to the first retained requirements. The last targeted revision changed only candidate-item `nullable: true → false`; it did not alter lookup, operations or unrelated tasks.
- Planning consumed **7 logical calls / 8 physical attempts**, **1 automatic repair**, **4 explicit revisions**, **2 discovery reads**, 73,940 verified input tokens and 17,838 verified output tokens. Planning time: 567,675 ms. No unknown usage for this run.
- Cost: **EUR 0.802946**. Campaign upper bound: **EUR 119.819403 / 150**, including unchanged EUR 6.501615 in historical reservations.
- YAML: 75,286 bytes, 34 steps including 15 sets, two mapping definitions and one actual business loop. These are this composition's measurements, not a before/after compiler claim.
- Actual provider request sizes and extraction correctness remain runtime checks. Static review does not prove semantic completeness, real-site availability or execution success.
- The separate local deterministic repair fixture passed real Browser navigation, ten visits, actual XLSX writing, independently checked cells and cleanup for both flatten and lookup repairs. It is not live acceptance.

This artifact has not been executed. It requires explicit acknowledgment of **all three requirement IDs (`success`, `blocked`, `cleanup`)**, the current revision and the exact artifact hash. Earlier approvals remain attached to earlier artifacts. Execution is once only; any uncertain invocation will not be replayed.

The earlier rejected review is retained in [revision 4 review](amazon-review-r4.md). The full solution passed with `-warnaserror`: 4,998 passed, 0 failed, 13 environment/opt-in skips. Package, Native AOT and focused/local gates also passed. CI results are recorded separately.
