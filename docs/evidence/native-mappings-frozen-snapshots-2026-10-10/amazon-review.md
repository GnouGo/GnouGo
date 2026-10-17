# Fresh Amazon proposal: rejected before execution

Frozen source: `6d2c65c4ecb329cc6da00e2b6b7b05ef930eb0d6`.
Run: `nativesnapshots20261010a-amazon-1`, revision **5**, status **stopped**.
There is no compiled YAML, artifact hash or execution approval.

The [accepted requirements](amazon-requirements.json) require Amazon.fr search,
at most ten products, each individual product visit, observed name/description/price
with explicit missing data, real Excel writing, Browser cleanup and truthful status.

## Compiler findings

| Path | Finding |
| --- | --- |
| `/tasks/extract_search_result_pages/each` | `each.input` is `page`, but the bound input is `pages`. The output `rows` is a flat record array, whereas a per-page candidate list followed by flatten requires an array of arrays. |
| `/tasks/read_search_results/requires` | `choice(submit_search)` names an operation, not a declared business choice. It cannot establish operation success. |
| `/tasks/select_first_10_products/inputs/flatCandidates` | `flatten` receives the flat `rows` contract rather than a typed non-null array of non-null arrays. |

Two automatic repairs returned the same `output(extract_search_result_pages, rows)`
replacement in the permitted value slot. Neither corrected the producer declaration
or the immutable `requires`. Both attempts and their receipts remain retained.
No repair authority or allowance was expanded.

## Review findings beyond compilation

- `inspect_home_controls`, `find_search_box` and `extract_product_fields` send whole
  observation snapshots to interpretation instead of bounded extraction and compact views.
- Selection returns complete regenerated product/action records, rather than IDs
  validated against offered candidates and resolved back to original observations.
- The model added consent interaction and URL deduplication, neither requested by
  the accepted requirements. These are model-generated proposal content, not
  instructions inserted by Browser or a new planner rule.
- Several interpretations copy or reassemble already typed records. The final path
  is passed through interpretation rather than exported directly from the writer.

The [retained proposal](amazon-retained-plan.json) is evidence, **not an executable
example or an approved artifact**. Fixing its three syntax/type errors alone would
not resolve these review findings. A later explicit revision must preserve accepted
requirements while correcting its data-adaptation composition; no such revision or
additional paid request was dispatched in this correction.

## Measured result

- Planning: 4 logical calls, 5 physical attempts, 2 repairs, 2 discovery reads,
  444,692 ms; 29,043 verified input / 10,987 verified output tokens; **€0.421355**.
- No new unknown transport attempts; all historical unknown reservations remain.
- Execution: not started. No Browser business navigation, product visits, learned
  runtime mapping or workbook creation occurred.
- E2E oracles: not run; **no live success claimed**. The separate local readiness
  and deterministic workbook fixtures passed and do not substitute for live evidence.
- Campaign upper bound: **€123.011199 / €150**, including **€6.501615** of retained
  unknown transport reservations.

See [exact planning/accounting result](amazon-planning-result.json) and
[committed response hashes and repair payloads](amazon-response-summary.json).
