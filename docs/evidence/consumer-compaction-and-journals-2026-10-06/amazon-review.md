# Amazon artifact awaiting explicit approval

- Frozen production/harness: `e2179af7d761f1f5d083671d0b9b1073a2e2b8fe`.
- Run/session: `consumerjournal20261006a-amazon-1`.
- Revision: **9**.
- Artifact hash: **`af84a2ca9690714e7c133a29cfa13843e69d62a7d9f25e782aa52dbda48839da`**.
- Input: `query = "chaussure geox homme 45"`.
- Destination: `workflows/schema-portability-20261002/consumerjournal20261006a-amazon-1/products.xlsx`.
- Status: generated and statically validated; **not approved or executed**. No acknowledgments have been submitted.

[Exact YAML](amazon-r9.yaml) and [requirements/TaskPlan/review](amazon-r9.json) retain the generated artifact. The requirements are unchanged from the first proposal.

## Requirements to acknowledge explicitly

- `search_amazon_fr`: La recherche est effectuée sur Amazon.fr avec la requête fournie.
- `max_three_products`: Seuls les trois premiers produits observés au maximum sont visités, dans l’ordre.
- `explicit_missing_data`: CAPTCHA, prix absent, description absente ou produit absent sont consignés explicitement dans le classeur.
- `save_xlsx`: Le fichier Excel est écrit au chemin relatif demandé dans l’espace de travail.
- `cleanup_browser`: Le navigateur est fermé via une étape always, même en cas d’échec.

## Actual data flow

1. Build the search URL, then open a bounded observation manifest. Require both original capture and manifest truncation flags to be false.
2. Read every frozen page cursor before interacting. Nested record loops export `kind`, `tag`, `text`, `group` and nullable `role` directly. They contain no inference task. The initial consent/blockage decision receives that view, page URL and status; it receives no selectors or link collection.
3. Conditionally click the observed consent text, then obtain and fully consume a fresh manifest. A separate global blockage decision receives text/control context. Global product selection receives the necessary text/context and nullable observed hrefs; selectors remain outside both consumers.
4. Visit up to three selected product URLs sequentially. Each visit captures and fully consumes its own manifest, then exports the typed text/context view. Product interpretation receives this view plus the current product identity; it receives no raw pages or incidental link collection.
5. Assemble the local workbook content and perform the real Document write. Return its actual file path/success result. Root `always` closes Browser after success or verified failure.

All page manifests and record loops remain bounded at 100; each producer page request is narrowed to 100 records. Product visits stay bounded at three. Missing/stale pages, incomplete capture, invalid data or necessary inputs exceeding the unchanged allowance remain failures. Unknown completion still blocks cleanup/replay.

## Inference and limits

Compaction uses typed projection: no learned mapping, no per-record interpretation and no `mapping.dynamic` steps in this artifact. Explicit interpret tasks remain for URL construction, initial state, search blockage, global selection, each visited product and workbook formatting: **at most eight runtime interpretation calls on the nominal three-product path**, before existing stricter host/budget enforcement. Selection necessarily retains observed hrefs; actual request sizes and field correctness still require live verification. No input allowance, output allowance, permission or execution oracle is raised or relaxed.

Planning so far: **6 logical calls, 8 physical attempts, 1 repair, 3 review revisions**, 2 discovery reads, 78,900 input / 27,657 output tokens, **EUR 1.092654**, 919,271 ms. Earlier revisions 3, 5 and 7 are retained as rejected review artifacts. Campaign upper bound is **EUR 86.696331 / 150**, including unchanged EUR 2.606753 historical unknown reservations. No new unknown usage exists.

Approval must acknowledge all five requirement IDs above and authorize this exact revision/hash. The existing approval command is submitted only after that explicit decision. Live product visits, observed XLSX cells, cleanup, actual request sizes and physical journal costs remain **unverified**. PR #117 stays draft.
