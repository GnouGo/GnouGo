# Amazon execution review: ten products maximum

**Status: awaiting explicit artifact approval. No workflow execution has started.**

- Frozen production/harness: `71315a5a7621f46f5a8b4ff1f3ab58213a5f445f`.
- Run: `compactbindings20261006d-amazon-1`; cohort: `compactbindings20261006d`.
- Revision: **8**.
- Artifact: **`0e86683a295778310311f8acda2d3c88a9b771a50cfdca30e940193fe24aad9f`**.
- Public input: `query`, evaluated with `chaussure geox homme 45`.
- Workbook: `workflows/schema-portability-20261002/compactbindings20261006d-amazon-1/products.xlsx`.
- [Exact YAML](amazon-d-r8.yaml), [TaskPlan, requirements, responses and review](amazon-d-r8.json), [artifact measurements](amazon-d-artifact-metrics.json).

## Requirements and actual work

| Requirement to acknowledge | Actual TaskPlan behavior |
| --- | --- |
| `searched_amazon_fr` | Open Amazon.fr, consume its observation manifest, select an observed consent control if present, conditionally click it, then capture and consume a fresh snapshot. Select an observed search control and submit the unchanged public query. Absence/blockage fails its explicit guard. |
| `products_extracted` | Consume every result cursor. Select at most ten observed product links from compact views. Visit each selected URL sequentially; consume that product's complete manifest, detect blockage, and extract observed name, description and price. Missing fields remain nullable; absence messages are composed separately. |
| `xlsx_saved` | Compose workbook rows from visited products and invoke the actual Document writer. Return the writer's `filePath`; a literal path is never treated as write evidence. |
| `browser_closed` | Root finalization closes Browser after normal completion or verified failure. Unknown external completion continues to block cleanup and replay. |

## Guards and data handling

Manifest loops reject `captureTruncated` or `manifestTruncated` directly from producer metadata. Each page projection rejects capture truncation; every manifest cursor is consumed before the next interaction/navigation. The model does not decide capture completeness. A capture that cannot be completed within existing bounds fails explicitly rather than producing fabricated coverage.

Four record-copy loops lower to deterministic projections, with no per-record invocation or inference. Consent/search-control views retain exact selectors; product-link views contain observed text/group/href without selectors; product-fact views contain text and structural context without unrelated URLs. Consumers reference named exports, not whole loop envelopes.

Interpretation remains explicit for control/link decisions, blockage classification and final report formatting. `mapping.dynamic` handles observed product-field extraction with its existing source-grounded sandbox, validation, cache and at-most-one-generation-plus-one-repair allowance per invocation. No synthesized status markers or missing-value substitutes are requested inside extraction. These static checks do not prove factual extraction correctness; execution and the independent XLSX oracle are still required.

There is **no startup approve/reject prompt**. This artifact review remains separate from MCP permissions, business questions and conditional handling of an observed cookie banner. No new permission, token ceiling or campaign extension is requested.

## Evidence and limits

The final solution passes **4,603 tests**, zero failures and twelve existing skips, with `-warnaserror`. Release packaging, planning Native AOT and published encrypted recovery evidence is linked from [the report](../../compact-bindings-validation-2026-10-06.md).

This run used **six planning calls/attempts, two automatic repairs, two review revisions and two discovery reads**: 66,235 input / 24,537 output tokens, EUR 0.947098 and 288,310 ms. Execution calls, cost and latency are still zero. The campaign upper bound is **EUR 90.553146 / 150**, including unchanged EUR 2.606753 historical unknown reservations.

The artifact has 33 business tasks across scopes, 13 workflows and 136 compiled steps; YAML is 110,213 bytes / 3,443 lines. It still contains explicit contracts, guards and runtime wiring. Historical r8 has 170,243 bytes / 5,361 lines / 223 steps, but that is a different proposal with a three-product limit, so this descriptive comparison is not a matched benchmark. Controlled fixture results are reported separately.

Approval authorizes exactly one execution of this revision/hash and acknowledgment of all four requirements above. Existing content/provenance, complete-observation, actual-visit, workbook-cell and cleanup assertions remain unchanged except for the explicitly authorized ten-product maximum. No code-review run, cohort expansion, GitHub feedback publication or uncertain-invocation replay is included. A successful single execution would still not establish the historical six-run acceptance gate.
