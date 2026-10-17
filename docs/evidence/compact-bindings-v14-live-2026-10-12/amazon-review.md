# Fresh v14 Amazon artifact review — awaiting execution approval

No workflow execution has started. This review concerns one newly generated artifact, not any historical approval.

## Frozen identity

- Candidate: `04213951a6020ecb484b1d0b190f06f112a03f82` (`compact-bindings-v14`).
- Campaign: `schema-portability-20261002`; unchanged ceiling **€200**.
- Run: `compactv1420261012a-amazon-1`; revision **7**.
- Artifact: `62cc3e938a7fe7f1b24c7ea4ffb8606d721ccf744cc5c463144f5ef8679e1ba9`.
- Query: `chaussure geox homme 45`; **at most ten products**.
- Expected workbook: `workflows/schema-portability-20261002/compactv1420261012a-amazon-1/products.xlsx` under the existing confined workspace.
- Oracle: unchanged `observed-products-v2`. The frozen source, 710 binary hashes, configuration, prompt and oracle remain unchanged. No source or financial-policy change was made for this run.

## Accepted requirements to acknowledge

1. `search_amazon_fr`: search Amazon.fr using the supplied query.
2. `collect_max_10_products`: process at most the first ten selected products in observed order.
3. `extract_product_fields`: retain observed name, description and price; missing facts remain null and errors come from producer evidence.
4. `save_excel`: write the actual workbook at the stated location.
5. `cleanup_browser`: close Browser on success or ordinary failure; existing uncertainty protections remain authoritative.
6. `truthful_completion`: do not report success if required acquisition, extraction or writing fails.

## Reviewed executable composition

1. Existing bounded query-to-URL interpretation, then Browser complete search acquisition.
2. Ordered local assertions require success, nonnull snapshot and both truncation flags false before projection/extraction.
3. Typed per-page original-record projection is separate from `extract.each`, which processes every complete page into compact arrays containing original reference and verbatim observed text. One-level flatten supplies the global decision.
4. Selection returns unchanged offered IDs (one to ten). Strict offered-candidate lookup precedes original-record lookup. The effectful visit loop uses original `href` values and preserves order/repetitions.
5. Each visited page is completely acquired and checked. Independent per-page extraction supplies compact fact candidates to a decision returning three nullable original IDs.
6. Each nonnull fact ID is checked against offered candidates and then original records. Typed projection copies a whole observed text block. The null alternative supplies `[null]`; it does not fabricate a replacement.
7. Bounded typed scopes assemble nonnull product records and cell arrays without inference. URL comes from the original selected navigation target; error comes only from Browser's `error_message`.
8. Writer rows are exactly `flatten([[headerRow], productRows])`, preserving the supplied header `nom, description, prix, url, erreur`, cell positions, whitespace and row order. Successful writer output supplies the path; its success and nonnull path are asserted before final output.
9. `browser_close` remains unconditional normal cleanup. No consent interaction, sponsored filtering, deduplication or extra business operation was added.

Five static inference stages remain: query conversion, search extraction, search ID selection, product extraction and product fact-ID selection. The product stages execute within visits. Adaptive mappings share the existing explicit runtime budget and validate every original item. Runtime inference call counts are not guaranteed by static stage counts.

## Validation and planning results

- Initial r3 and revised r5 artifacts were retained but not executed. Manual review rejected broad extraction/learned copying/header omission in r3, then remaining implicit interpretation for assembly and missing truncation guards in r5.
- Two existing explicit structural revisions preserved accepted requirements, public outputs, exact external operation definitions, ordering and cleanup.
- The final minimal replacement was compiled offline against retained contracts before submission. The generated r7 contains only those replacements; it independently recompiles byte-for-byte to the retained YAML through the normal compiler and verifies its artifact.
- Final review has zero diagnostics. **28 TaskPlan tasks; 7 workflows; 26 steps; 5 sets; 217,493 YAML bytes; 19,843 JavaScript bytes; zero technical-named declarations; one retained wrapper; zero `m.select` calls.** Necessary pure scopes do not add inference.
- Candidate validation remains green: full solution previously passed **5,461 tests** with **13 existing skips**, no failures/warnings; **11 local Browser/Document oracle executions** passed. Planning Native AOT, release/package, published encrypted recovery and skill validation passed.
- Fresh CI recheck: **29 successful checks**, four conditional publishing skips; PR remains draft. Fresh local Browser/real XLSX readiness and configured pricing/currency readiness passed. These checks do not prove the future Amazon result.
- Planning: **5 logical calls, 1 repair, 2 explicit revisions, 2 discovery reads, 8 physical transport attempts**. Active cumulative planning time **1,215,385 ms**. Known usage **68,016 input / 24,410 output tokens**, known cost **€0.951619**.
- One transport attempt remains uncertain, with **€1.378255** conservatively reserved. The associated logical request has a committed response. No uncertain attempt was replayed or reclassified.
- Campaign upper bound: **€141.320849 / €200**, including all **€11.902902** retained reservations; approximately **€58.679151** remains. All planning transport attempts are consumed; no reset or additional planning call is authorized by this review.

## Execution and independent acceptance

Execution has **not started**: zero execution model/MCP calls, no Amazon product visits and no output workbook. The local readiness probe is separate.

After explicit approval of this exact revision/hash and all six requirements, execute once under unchanged permissions and runtime/campaign ceilings. Independently verify complete observations, selected original URLs, actual product visits, exact source-associated cells, headers/order/nulls, output path and Browser cleanup. A completed workflow alone is not acceptance. Retain any failure and uncertainty without replay; keep PR #117 draft until the real oracles pass.

The planning skill requires: “Artifact approval requires explicit `ReviewedRequirementIds`, current revision and artifact hash. Never populate acknowledgments automatically; compilation is not proof of business completeness.” No acknowledgment or execution approval has been recorded for r7.
