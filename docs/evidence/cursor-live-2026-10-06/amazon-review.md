# Amazon validation artifact review

Execution subsequently failed during journal persistence. This run has started and must not be replayed. See the [retained outcome](../../cursor-live-2026-10-06.md); the reviewed YAML and approval command below are unchanged.

- Frozen production/harness: `26cf8c63101f456f6409c8891072d2d5fdca7a01`.
- New run: `cursorlive20261006a-amazon-1`; cohort: `cursorlive20261006a`.
- Reviewed revision: **8**.
- Artifact: **`ef62b18563a5b87fb1172a449397a0464e06aff57abe6014b7d5239e845865c4`**.
- Input: `query = "chaussure geox homme 45"`.
- Workbook: `workflows/schema-portability-20261002/cursorlive20261006a-amazon-1/products.xlsx`.

[Exact YAML](amazon-r8.yaml) · [Accepted requirements, TaskPlan and validation](amazon-r8.json) · [Bound approval command](approved-r8-command.json).

## Authorization and reviewed requirements

The user explicitly approved the next live validation after previously acknowledging the four business requirements. Manual review confirms the same scope below, with unchanged permissions, execution oracle and EUR 150 campaign ceiling. The [authorization record](authorization.json) binds that approval to this new artifact; it does not authorize replay of an existing run or a second execution.

1. `search_amazon_fr`: search Amazon.fr using the exact public query after conditional handling of an observed consent control.
2. `extract_up_to_three_products`: visit at most the first three observed products and extract observed name, description, price, URL and status.
3. `save_excel`: create the actual workbook at the declared workspace destination.
4. `explicit_blockers_and_cleanup`: retain explicit CAPTCHA, missing data, no-results and incomplete-capture behavior; close Browser on success or verified failure. Unknown external completion still prohibits cleanup and replay.

Accepted requirements are byte-equivalent as JSON values across this run's revisions. Rejected revisions 3 and 5, feedback and provider responses remain retained.

## Actual composition

1. Read Document policy, open the homepage and capture a typed manifest. Require both original truncation flags to be false; consume every listed page before interaction. Check each page's capture flag and snapshot identity without forbidding continuation on intermediate pages.
2. Copy every record through direct typed fields, preserving order and duplicates. Home/control views carry `kind`, `text`, `role`, `group` and the exact `selector`, because the subsequent observed control requires it; they omit URLs.
3. Interpret the control view, conditionally click the observed consent selector, then capture and fully consume a fresh homepage snapshot. Fill and submit the exact query using the observed search control.
4. Capture and completely consume the results snapshot. Build separate views: status gets `kind/text/group`; product selection gets `kind/text/group/href`. The consumers receive their own view, with no raw observations or combined superset. Select up to three observed product URLs globally and in observed order.
5. Visit every selected URL sequentially. Capture and consume its complete snapshot, exporting only `kind/text/group` for product interpretation, plus the explicit current product URL/context. Preserve truthful missing-data and blocker status.
6. Format at most three rows and write through the real Document operation. Blocker branches also use actual workbook writers; a status workbook does not pass the successful-product oracle. Return the writer's result. Root cleanup closes Browser.

Bounds remain 100 manifest pages, 200 records per page loop and three products. Truncated capture or an oversized necessary interpretation remains an explicit failure; there is no invented completeness or raised allowance.

## Inference and limits

No `mapping.dynamic` or per-record inference appears in this artifact. Typed exports perform compaction. The nominal three-product path has **14 explicit runtime interpretations**: two homepage/control interpretations, search status, global product selection, three interpretations per visited product, and final workbook formatting. This remains a measured efficiency limitation, not a one-call execution claim. Existing request admission, cumulative accounting and the execution deadline apply.

Planning: **6 logical calls / 8 physical attempts**, **2 automatic repairs**, **2 review revisions**, 2 discovery reads; 73,087 input / 31,791 output tokens; EUR 1.177405; 1,263,274 ms. All new usage is verified. The planning attempt and repair allowances are exhausted and have not been reset. Campaign upper bound before execution: **EUR 88.635067 / 150**, retaining EUR 2.606753 historical unknown reservations.

This review establishes an executable composition to test. Only the unchanged independent execution oracle can establish live success. PR #117 stays draft until its full acceptance gate passes.
