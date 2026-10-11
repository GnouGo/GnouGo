# Amazon retry artifact review

- Frozen production/harness: `716d4b5510a3759fb54283eb1e9d6476b0adcc5e`.
- Run: `consumerretry20261006b-amazon-1`; cohort: `consumerretry20261006b`.
- Revision: **6**.
- Artifact: **`e4229b3d32b7375133feb9962e00a0bf0d517ee451d1fa15db887d445cb6ab0d`**.
- Input: `query = "chaussure geox homme 45"`.
- Output: `workflows/schema-portability-20261002/consumerretry20261006b-amazon-1/products.xlsx`.
- Status: all four requirements explicitly acknowledged by the user; **executed once and failed on the first observation continuation**. The approval advanced the saved revision to 7 without changing this artifact. Browser cleanup completed. This run must not be replayed.

[Exact YAML](amazon-r6.yaml) · [Requirements, TaskPlan and review](amazon-r6.json).

## Explicitly acknowledged requirements

1. `search_amazon_fr`: Amazon.fr is searched using the provided query.
2. `extract_up_to_three_products`: At most the first three products are visited and extracted individually.
3. `save_xlsx`: The workbook is written to the exact output path above.
4. `cleanup_browser`: Browser is closed even if search, extraction, CAPTCHA or file creation fails.

The accepted summary also requires observed values only, explicit missing data and a single public query input. These requirements are unchanged from the initial proposal.

## Actual composition

1. Interpret the query into a search URL. Capture a bounded page manifest. Require original `captureTruncated=false` and `manifestTruncated=false` before consuming its pages.
2. Read all listed page cursors sequentially before navigation or interaction. Require each page's capture to be complete. For each declared record, export only `kind`, `tag`, `text`, `group`, `role` through direct typed bindings. Empty loop bodies perform no inference.
3. Interpret complete text/control views to identify an observed consent control. Click its exact visible text only when present, then capture a fresh manifest. Both conditional branches expose the same manifest output.
4. Consume all fresh search pages under the same guards. Product selection gets one deterministic `kind/tag/text/group/role/href` view per page, with grouping, exact cursor and record count; selectors and duplicate blocker records are excluded. It globally chooses up to three observed organic product URLs in display order and reports observed blockage/no-result status.
5. Sequentially visit each selected URL. The product group captures and consumes its manifest completely, then passes only typed text/context records plus the product URL to interpretation. Names, descriptions and prices must be observed; missing values remain explicit nulls/status.
6. Interpret the at-most-three product rows into TSV, write the actual workbook through Document with `append=false` and UTF-8, and return the writer's real path. Root `always` calls Browser close. Unknown external completion continues to prohibit cleanup/replay.

Bounds: 100 manifest pages, 200 records per requested page and record loop, three products. No limits or permissions changed. The generated plan uses explicit page-region selectors with a body fallback; real-site rendering, completeness and relevance remain subject to the unchanged independent oracle, not assumed from compilation.

## Inference and evidence

No `mapping.dynamic`, learned extraction or per-record inference appears in this artifact. The nominal three-product path has **seven explicit runtime interpretations**: URL, initial state, global product selection, three product extractions and workbook formatting. Existing request/campaign bounds still apply. Necessary compact views can remain too large on real pages; they must fail before dispatch rather than truncate.

Planning: **4 logical/physical calls, 0 repairs, 2 review revisions, 2 discovery reads**, 53,750 input / 19,354 output tokens, **EUR 0.758095**, 200,439 ms. Campaign upper bound **EUR 87.454426 / 150**, including unchanged EUR 2.606753 unknown reservations. Browser/Document local readiness and independent XLSX reading passed with zero inference. Rejected revisions 2 and 4 and their feedback are retained.

The user's “I approve the four requirements !” authorized the [revision/hash-bound command](approved-r6-command.json). Runtime confirmation was submitted under the same authority. The immutable artifact was executed once; the [oracle failed](execution-oracle.json) because Browser rejected a valid manifest cursor when requested as `observation`, before product visits or workbook creation. The [retained events](execution-observations.json) establish successful Browser cleanup and verified completion of the failed MCP call. No unknown invocation was resumed.

Execution used one model call, 113 input / 102 output tokens, EUR 0.003235 and 36,769 ms. Campaign upper bound is EUR 87.457661 / 150, retaining EUR 2.606753 historical unknown reservations. [Final accounting](execution-result.json). PR #117 remains draft. A producer-side cursor correction is validated separately; it does not turn this failed execution into a success or authorize its replay.
