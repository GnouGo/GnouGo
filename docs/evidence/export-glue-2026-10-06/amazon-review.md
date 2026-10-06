# Amazon artifact awaiting explicit approval

This is a **new, unexecuted** artifact. No prior approval covers it. The frozen production and harness are `7adab9ee1ff84557742727f91bb23098f23f5052`; the cohort is `exportglue20261006a`.

- Run: `exportglue20261006a-amazon-1`
- Revision: **11**
- Artifact hash: **`1557a5d6c72c4c30510b0b4e9b08050ae4b8d5c27dc0bd47545ac98d0df238e1`**
- Input: `query = "chaussure geox homme 45"`
- Output: `workflows/schema-portability-20261002/exportglue20261006a-amazon-1/products.xlsx` inside the existing confined workspace.
- [Exact YAML](amazon-r11.yaml) · [TaskPlan, accepted requirements, review and accounting](amazon-r11.json) · [Frozen manifest](manifest.json).

## Requirements beside the proposed work

| Required acknowledgment | Actual tasks and bindings |
| --- | --- |
| `search_amazon_fr` | `build_search_url` uses the query; `capture_initial_search` opens it through Browser. `detect_consent` extracts observed consent text; the conditional clicks that exact text only when present. `capture_search_results` takes a fresh observation afterward. |
| `limit_first_10` | Global `select_search_products` chooses at most ten candidates from the complete selection view. The sequential `process_products` loop independently enforces `maxItems: 10`. |
| `per_product_visit_extract` | Each candidate URL is passed to a real Browser operation inside `capture_product_page`; `extract_product_fields` receives that page's projected observations and observed URL. Missing fields stay nullable and the final interpretation must report them truthfully. No search summary substitutes for product visits. |
| `save_xlsx` | `prepare_workbook` formats the extracted records; the real `document_write` operation writes the TSV content to XLSX. `workbookPath` comes from the operation result, not a literal success claim. |
| `cleanup_browser` | The single root finalizer calls `browser_close`. Normal export glue is in ordinary steps; runtime finalization limits and unknown-completion safeguards are unchanged. |

## Observations and inference

The shared capture group requests a bounded immutable manifest with at most 100 records per page, requires authoritative capture and manifest truncation flags to be false, and consumes every listed page before navigation. Individual pages also require `captureTruncated: false`. A listed page's continuation is not mistaken for incomplete capture; all manifest pages must be read. Capture requiring narrowing stops explicitly instead of guessing a selector or manufacturing an empty result.

Three copy-only projections process every record, preserving order, duplicates, nullable values and per-page nesting. The selection view carries `kind/group/text/href`; consent carries `kind/role/group/text`; product-field extraction carries `kind/group/text` plus the observed page URL. Raw pages remain retained source evidence but are not redundantly passed to those transforms. These copies compile deterministically, with zero per-record inference or workflow invocation.

Three explicit interpretation sites remain visible: URL construction, global candidate selection and workbook formatting. Consent and each visited product use bounded runtime extraction, at most one generation plus one repair per invocation, with validated cache reuse. Actual request sizes, inference, extraction correctness and external page availability remain unverified until execution. The unchanged provider/request/campaign ceilings apply; oversized necessary inputs stop rather than truncate. CAPTCHA, missing observations, failed validation or external errors cannot count as a successful oracle.

The compiled artifact has 73 steps across eight workflows: 47 `set`, nine workflow calls, five MCP calls, three templates, three interpretation calls, two dynamic mappings, two switches and two real loops. There is **one finalizer**. The three record-copy loops become checked compiled projections, not additional runtime loops. YAML is 96,810 bytes / 2,859 lines including descriptions and schemas. These are compiled counts, not measured execution counts.

## Current evidence and approval boundary

Planning reached review after seven calls/physical attempts, zero automatic repairs, two discovery reads and four explicitly scoped review revisions. Known planning usage is 93,000 input / 26,785 output tokens, EUR 1.125699; active planning latency is 317,835 ms. Initial proposals and revision commands are retained. The final targeted revision only adds `maxRecords: 100` to the manifest request; all other plan content and accepted requirements are unchanged from revision 9.

Execution has **not started**: no live XLSX, product-visit oracle or cleanup measurement is claimed. Approval must acknowledge all five IDs above and bind this revision and hash. The campaign upper bound is EUR 91.896721 / 150, including EUR 2.606753 unchanged historical unknown reservations. No code-review run, cohort expansion or uncertain-invocation replay is included.

The unchanged independent oracle checks real product visits and complete captured observations, XLSX row/column shape and observed cell values, exact output location and Browser closure. A later failure is retained; it does not authorize blind replay or a weaker oracle.


## Subsequent execution record

The user subsequently approved this exact revision/hash and all five requirements. It executed once and failed request admission before consent extraction, with successful workflow cleanup. The review above is preserved as the pre-execution record. See the [execution report](amazon-execution.md); do not replay this completed invocation.
