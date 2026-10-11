# Amazon planning review — execution blocked

Run: `compactv1020261011a-amazon-1`, frozen candidate `5a8afbfc`, revision **5**.
Artifact: `c5e0562dc246190a18014cfb4d7b41a252ff04c3e8aef4d7f22cf208f51485bf`.
The compiler reports `final_review` with no diagnostics. This is **not execution
approval**. No requirement acknowledgment has been populated and no workflow
execution has started.

The six accepted requirements remain unchanged: `search_amazon_fr`,
`collect_products`, `extract_details`, `save_excel`, `cleanup_browser`,
`truthful_status`. Product objects remain non-null, their permitted missing fields
remain nullable, and the public collection retains its zero-to-ten bound.
Search navigation, individual product navigation, the actual Document writer and
root Browser cleanup remain present. No consent operation was added.

## Blocking findings

1. `project_search_page_records` and `project_product_page_records` use learned
   extraction to copy already typed records. Their output is then treated as the
   authoritative original collection. These must be existing deterministic page
   projections over the retained producer records; provenance alone is not proof
   that a learned copy preserved every identity or value.
2. `assemble_product_detail` sends flattened records and selected IDs to another
   learned mapping. It does not perform the required deterministic offered-ID
   validation and original-record lookup for name, description and price. The
   selected complete description must come from its original observed block.
   `checked_products_projection` also uses extraction for a typed copy/check.
3. The offered candidate view still carries action URLs and derived fields into
   selection. Narrow the explicit contract to the actual decision fields and keep
   authoritative action arguments on the original-record path. This is a review
   finding for this composition, not a compiler relevance heuristic.
4. `build_xlsx_rows` flattens only the header array into `string[]`. The targeted
   repair changed the writer's `rows` binding to `scrape_products.row`, satisfying
   its array-of-arrays type but bypassing the header completely. The unused header
   task remains. With N visited products the writer receives N rows, while the
   unchanged independent oracle expects a header plus N product rows.

The zero-inference, zero-MCP [oracle witness](header-oracle-proof.json) rejects the
same observed product row without a header (`product_visits_do_not_match_rows`,
`workbook_columns_missing`) and accepts it with the supplied header. It invokes the
frozen independent oracle on synthetic generic evidence only; it does not execute
or approve this workflow or establish a real workbook result.

## Minimal next correction

Use the existing typed per-page projections, one-level flatten, guarded strict
lookups and direct scope exports. Preserve both offered-candidate and original-record
checks, nullable branches and the necessary bounded product check. Assemble
`[headerRow, ...productRows]` with the existing correctly nested array/flatten binding
at the writer; remove the unused task. No new primitive, executor, validator
exception or repair authority is needed. Validate the corrected retained
composition offline before proposing more paid planning.

The current session has used all eight admitted transport attempts. Do not reset
that allowance, replay its uncertain attempt, directly edit its saved YAML, or
request execution approval merely because compilation passed. Preserve its four
committed logical receipts and its additional unknown reservation. Further paid
work needs new authorization; this artifact is not suitable for execution.
