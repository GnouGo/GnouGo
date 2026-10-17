# Amazon execution review — revision 9

**Awaiting explicit artifact approval and acknowledgment of all five requirements.**
The user's `YES GO` authorized fresh paid planning. It did not approve this newly
generated artifact. No live Browser action or workbook write has started.

- Run: `typedbindings20261009b-amazon-1`
- Frozen candidate: `7a76fc951d31dec26978f6c01a07b76ae71ea154`
- Revision: **9**
- Artifact hash: **`cbcd077db834ebe6da54b25bb838e5b6cca897d845a52a29484fcbb21b6cdb89`**
- Input: `query = "chaussure geox homme 45"`
- Product maximum: **10**
- Workbook: `workflows/schema-portability-20261002/typedbindings20261009b-amazon-1/products.xlsx`
- [TaskPlan](plan-r9.json), [compiled YAML](amazon-r9.yaml),
  [accepted requirements](requirements-r9.json), [compiler review](review-r9.json).

## Requirements beside the proposed work

| Requirement to acknowledge | Actual tasks and data dependencies |
| --- | --- |
| `search_amazon_fr` | `build_search_url` prepares the query URL; `open_amazon_search` navigates with complete acquisition. Compact gate facts feed `detect_search_gate`. Conditional consent uses an offered ID, original-record lookup and Browser's `activate` reference check. The page is observed again afterward. |
| `collect_products` | Complete search pages feed independent compact extraction. Pure item-scope projections and one-level flattening collect offered candidates. `extract_first_product_links` selects at most ten observed IDs in observed order, without sponsored filtering or deduplication. Two lookups validate offered IDs and reconnect original records. |
| `extract_product_details` | `scrape_products` iterates the reconnected records. `open_product_page` requires a non-null original `href` and navigates to it. Every acquired product page is processed; compact name/description/price candidates feed one row decision. Each actual product yields one non-null row; missing fields may be null with explicit status. |
| `save_excel` | `finalize_rows` copies the loop's typed rows directly, preserving `maxItems: 10`. The retained bounded `make_excel_tsv` transform formats those rows. `write_products_xlsx` calls Document to write the real workbook. Public `filePath` comes from the writer's output. |
| `close_browser` | `close_browser_always` remains in finalization. Existing recovery rules still prohibit cleanup and replay after genuinely unknown completion. |

## Data adaptation and inference being approved

Four page-extraction stages expose narrow typed views for gate detection, scope
choice, product selection and product facts. Original observations remain separate.
Each stage processes complete pages through the existing adaptive mapping binding;
examples may be sampled for program generation, but every original item must
validate before publication. Existing runtime/campaign allowances apply to all
generations and specializations.

Interpretation receives compact fields, rather than a raw snapshot alongside them.
Selection returns IDs. Pure item projections, flattening, ID reconnection and final
row collection are deterministic. Lookup preserves selected order and repetitions;
it rejects unknown or ambiguous identities.

One additional **scalar extraction** adapts the zero-or-one original selector array
to Browser's nullable selector argument. It receives the observed selectors plus
the explicitly declared null for the existing no-selection behavior. Source-grounded
mapping replaces unconstrained interpretation here; its historical scalar allowance
is one generation and one repair. An offline sandbox check covered a present
selector, no selection and explicit null. This adapter is still runtime inference,
not a claim of deterministic TaskPlan indexing.

Six interpretation definitions remain: query URL formatting, gate decision, scope
decision, candidate selection, product-row selection and bounded TSV formatting.
Product-row selection executes once per visited product. URL formatting is still
model-driven; the Browser producer's navigation policy and the independent query
oracle remain necessary. No new URL encoder or array-index operator was introduced.

The retained snapshot producer must return a complete acquisition or an explicit
failure. No CAPTCHA, successful empty result, missing field or workbook success is
inferred from an unusable acquisition. Schema validation alone cannot establish
that the selected facts are semantically complete or truthful.

## Validation and limits

- The fresh revision passes current compilation and validation with no diagnostics.
  [Read-only replay](replay-r9.json) makes zero model calls and changes no saved state.
- Accepted requirements are unchanged. The last revision changed only the approved
  `select_scope_selector.mode` and `.inputs` paths.
- The prior focused offline evidence includes real local Browser/Document execution,
  independently inspected XLSX cells, permission failures and encrypted recovery:
  [offline validation](../typed-bindings-offline-2026-10-09/README.md).
- This artifact has **47 TaskPlan tasks, 50 compiled steps, 16 sets and 153,302 YAML
  bytes**. These are static counts, not measured live execution costs.
- Planning used **6 logical calls, 7 physical attempts, 0 automatic repairs,
  3 explicit review revisions and 2 discovery reads**. Verified usage is
  **67,285 input / 23,042 output tokens**, cost **€0.911958**, planning time **652,335 ms**.
- Campaign upper bound: **€117.691596 / €150**, including **€6.501615** in unchanged
  historical unknown reservations; approximately **€32.31** remains.
- The pinned model, 96,000-token input allowance, permissions, execution deadline,
  accounting admission and independent execution oracles remain unchanged.

Approval would authorize **one first execution of this exact artifact**, followed
by independent checks of real navigation, product visits, source-backed workbook
cells, output location and cleanup. It would not authorize replaying any uncertain
invocation, another cohort, code review or an increased budget.

**Execution: not started. Live XLSX oracle: not run. PR #117 remains draft.**
