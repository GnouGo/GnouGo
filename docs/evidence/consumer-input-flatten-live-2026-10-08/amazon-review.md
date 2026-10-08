# Concrete Amazon artifact review

Run `consumerflat20261008a-amazon-1`, candidate `b46803db4846343806caf1dacf10ce66ac30e4c9`, revision **12**, artifact **906f064e5ac78b36c38ca9e57cf4608934d574f52357efaf80c4c5ce97282d06**.

The user explicitly approved this fresh live validation. The following manual requirement-by-requirement review applies that authorization to this single artifact, using its current revision/hash. No historical approval is reused and acknowledgments were not inferred automatically from compilation.

| Accepted requirement | Reviewed implementation |
| --- | --- |
| `search_amazon_fr` | `open_amazon` requests complete acquisition. Every snapshot page passes through independent candidate extraction and explicit one-level flatten. Interpretation selects observed references. `accept_cookies_if_present` guards an `activate` call by non-null reference and no detected block, then obtains fresh observations; branches expose matching typed state. Search uses the observed fill reference and accepted query, with a null-check branch. Browser owns target/action validation. |
| `collect_products` | Complete search acquisition is independently extracted across every page; flattened candidates and original completeness metadata enter the global selection. `visit_each_product` is sequential, maximum ten. No raw snapshot is passed into global interpretation. |
| `extract_product_details` | Each selected product has an actual navigation and complete acquisition, all-page extraction, one-level flatten and explicit interpretation of observed candidates. The final targeted revision removes search-list fallback input: only product-page candidates, observed URL and completeness metadata remain. Nulls and observed blockers remain explicit. |
| `save_excel` | `build_products_tsv` visibly interprets collected typed products into TSV. The real `write_products_xlsx` operation runs at root after collection, uses the fixed authorized destination, and supplies the public `products_file` from its returned filePath. Root products reference the enclosing collected export. |
| `cleanup_browser` | Root `always` invokes the resolved `browser_close` operation after success or verified failure. Unknown completion retains cleanup/replay protection. |

Four explicit extraction bindings disclose adaptive runtime inference under the existing shared finite runtime/campaign budget. All original items must validate, even when generation examples are bounded; no partial result publication. Full observations remain separate. The global interpretation input contracts contain selected facts rather than page envelopes. Their actual complete request sizes remain subject to the unchanged 96,000 allowance. Review does not certify semantic relevance, successful execution or oracle correctness.

The independent oracle still requires real product visits and product-page support for XLSX names/descriptions/prices, valid output location and Browser cleanup. A truthful blocked/missing-data workbook is not a successful live oracle. No permissions, limits or oracle assertions changed.

Planning: seven logical and physical calls, two reported discovery reads, four explicit revisions, zero automatic repairs. Earlier rejected proposals and revision feedback are retained. Revision 8 also exposed a repair-schema nesting preflight blocker (12 levels, maximum 10), consuming no repair call; it remains an open generic issue. Final input-only revision changed no other TaskPlan fields or accepted requirement.
