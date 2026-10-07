# Concrete review for the authorized next Amazon validation

Candidate: `ee24b8796c96a9da38b3adcdefea5e20cacfd5ca`.
Run: `typedbranch20261007a-amazon-1`, revision **9**.
Artifact: **`2a39b345f7b5715c9b124a688f3854992244d1ffa2df81ee50bed7cb32d1ad92`**.

The user's current implementation request explicitly authorizes the next fresh Amazon validation. This review applies that authorization to this artifact only; it does not reuse an older artifact approval. The six IDs below were individually reviewed against the actual TaskPlan before creating [the concrete approval command](approval-r9.json). No UI recommendations or acknowledgment controls were automatically selected, and the harness validation remains unchanged.

| Accepted requirement | Actual implementation reviewed |
| --- | --- |
| `search_amazon_fr` | Only public input is `query`. Complete initial Browser acquisition, per-page extraction, observed cookie control under a null guard, fresh acquisition after click, then observed search selector consumed under an exact non-null conditional. No guessed action selector remains. |
| `list_first_products` | All delivered result pages are independently compacted before global ordered candidate selection. `t_visit_each_product` is sequential and bounded to ten items; excess results fail the existing bound. |
| `visit_each_product` | Each selected candidate URL drives an actual `browser_get_content` operation in the iteration body. |
| `extract_product_fields` | Complete per-product acquisition feeds per-page extraction and compact interpretation; nullable name/description/price and explicit status preserve missing-data reporting. Source completeness metadata is read directly and required before each mapping. |
| `save_xlsx` | The real `document_write` operation consumes generated tabular content and writes the fixed run-specific `products.xlsx`. Exports reference the operation's returned file path. The independent oracle must verify actual workbook cells against observations. |
| `cleanup_browser` | Root `always` retains the actual `browser_close` operation. Existing unknown-completion restrictions remain effective. |

The cookie alternatives now expose identical named metadata ports and identical `pageFacts` item schemas. Each branch uses its own source observations. Interpretation receives compact facts rather than raw snapshots. All four extraction tasks declare explicit `each`, with the existing maximum of one generation and one repair per invocation; stricter host/workflow/campaign limits prevail. Existing explicit interpretation remains visible in the artifact.

This is implementation review, not a claim of successful execution or factual extraction. The unchanged oracle decides execution acceptance; CAPTCHA, missing observations, mapping failures, budget refusal or cleanup failure cannot count as success. No code-review execution or cohort expansion is authorized here.

Earlier artifacts are retained: revision 3 contained an unobserved selector and raw global inputs; revision 5 still used global extraction of snapshot objects; revision 7 needed matching page-fact item fields and completeness checks before inference. Their approvals were not reused. The last revision changed only those four extraction tasks (guards/dependencies, plus the after-cookie result interface and objective); no operations or other task fields were removed.
