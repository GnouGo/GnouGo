# Bounded observation generation — 18 October 2026

This follow-up addresses the [failed Amazon extraction](../first-attempt-amazon-live-2026-10-18/README.md) through generic generation guidance and bounded diagnostics. It does not increase request, collection, iteration or campaign limits, rewrite historical artifacts, or introduce a runtime partitioning operator.

## Baseline and scope

The source and fixture hashes in [baseline.json.gz](baseline.json.gz) were recorded before implementation. The previous frozen live source is `e880d32e80a268fde77df11fec661aab0793e48b`; the original branch remains at `676b5acef3442b1436149eaf9581c7d5e99fe1a3`. The earlier Amazon run `firstattempt20261018a-amazon-1` remains failed and immutable. Changes already present in the main working tree were preserved.

Fresh proposal schemas retain a concise `each` annotation through compaction and size fallback: bounded learning examples do not limit processing, which covers all items in order with repetitions. Fresh guidance separates input allowances from output bounds and iteration ceilings, limits each consumer to its necessary fields, preserves original sources and cross-item relationships, and does not infer independence from pagination. No site, selector, filter or fixed composition recipe was added.

The existing benchmark execution adapter now reports the conservative token estimate of the complete serialized dispatch request, the retained limit, unit and `not_started` status. This is the same admission guard and estimator; it is not a new production-wide guard. Existing mapping admission uses serialized UTF-8 bytes plus framing as a conservative bound and keeps its distinct label. The host displays single-request admission separately from cumulative budget exhaustion. Explicit revision context receives only allowlisted measurements and locations, not observations or scripts. Automatic correction stays blocked.

TaskPlan, format 10, compiler v15, issued schemas, approvals, receipts, limits and public contracts are unchanged. The only runtime additions are extensible diagnostic fields. Planning, extraction and benchmark documentation were updated.

## Deterministic validation

New regressions failed before the corresponding fixes. The large synthetic refusal uses 53 pages and 1,581 records, verifies zero provider dispatches for the refused extraction, completed cleanup and unchanged diagnostics after durable resume. A separate typed projection fixture preserves all 1,581 records, exact repeated Unicode values, nulls, long original identities and a relationship crossing a page boundary. It uses zero mapping inference and one scripted global decision on the complete necessary facts.

Existing compact-observation cases cover relevant items outside learning examples, empty/incomplete collections, missing data, oversized individual items and a still-oversized global decision. Existing host compositions independently check downstream extraction, exact workbook cells and truthful completion. Fixture results do not demonstrate spontaneous live-model composition.

The retained presentation comparison continues to report fixed-context size. It no longer asserts that every request must shrink when useful semantic guidance is added; required-contract visibility and the exact 24,000-token request ceiling remain asserted. No contract or admission threshold was relaxed.

The 33-project serial solution pass executed 6,068 cases: 6,041 passed, 13 were skipped and 14 unchanged ProxyCopilot HTTP tests timed out. All 177 proxy tests then passed in isolation in 9.54 seconds, without changing code, timeouts, assertions or budgets. Across the retained pass and rerun, all 6,055 executable cases passed; an uninterrupted clean solution pass is not claimed. Planning passed all 1,619 cases and Agent.Server passed 1,111 with one existing opt-in skip. The frontend build, Planning Release package and isolated Debug benchmark build passed without warnings. See [validation](validation.json) and the retained [log manifest](log-manifest.json). No packaging, public serialization, journal format or AOT boundary changed in this follow-up; published encrypted-recovery and Native AOT checks from the previous implementation are not rerun solely for additional JSON diagnostic fields.

## Live protocol

The authorized follow-up uses a new Amazon identity under `schema-portability-20261002`, the retained EUR 200 cumulative ceiling, the pinned provider/model/settings, existing contracts and `observed-products-v2` oracle. It preserves all earlier unknown reservations. Planning evaluates the generated first proposal without replacing it with a handwritten plan. Execution requires explicit review of the new requirements, revision and artifact hash. Complete three repetitions only after success and while existing terminal conditions and cumulative admission permit.

An Amazon result cannot establish the separate general 25% first-proposal business-error reduction gate. The earlier full comparison remains inconclusive.

## Frozen candidate

Candidate `00b1aa751bbdd8787cda25f425a56db2e6850f51` is a clean detached local checkout. Its production and test files match the validated main working tree; the main branch and index were not committed or reset. The [freeze](freeze.json), [source hashes](candidate-source-files.json.gz), [708 binary hashes](binary-hashes.json.gz) and [initial accounting](accounting-before.json) were captured before paid dispatch. Local readiness passed without inference. Initial campaign usage plus reservations remains EUR 157.181492 of EUR 200; unknown usage is retained.

## New Amazon first proposal: review before execution

The authorized new run `boundedobs20261018a-amazon-1` reached FinalReview on its first submitted proposal (logical call 2): two discovery reads, two physical attempts, zero repairs and no structural diagnostics. Planning took 86,745 ms, consumed 19,828 input and 7,030 output tokens, and added an estimated EUR 0.277292. There were no new unknown attempts. Campaign usage plus retained reservations is EUR 157.458784 of EUR 200.

Read-only review **does not establish the intended live improvement**. `detect_consent_gate`, `extract_first_products` and `extract_product_detail` each receive a complete `observationSnapshot`, with no preceding narrowing and no explicit `each`. The first two operate before product visits. The ten-product output bound still does not bound those requests. At review time, a renewed admission rejection was plausible, but no new observation had been acquired. The subsequent execution and actual measurements are recorded below.

The plan also uses interpretation for workbook row assembly and introduces a conditional `browser_click` on the observed consent control it judges least permissive. This effect has no established exact requirement association and remains **unverified**, not automatically unauthorized. Browser closure is declared in the always-run scope. Workbook operation presence and supported output lineage are verified; content fidelity and successful execution are unverified.

Revision 2 has artifact hash `180bd0c81ef4dee79c253bb6911277d44b78c9d9e275eddfff84016c5f51bc9f`. The five accepted requirement IDs are `search_amazon_fr`, `bounded_product_processing`, `extract_product_fields`, `save_workbook` and `cleanup_browser`. Original-request review also covers truthful completion. The fixed workbook destination is `workflows/schema-portability-20261002/boundedobs20261018a-amazon-1/products.xlsx` under the configured workspace.

The user explicitly acknowledged all five requirements, the additional consent click and diagnostic execution after reviewing revision 2 and its artifact hash. The unchanged artifact was approved at revision 3 and executed once. The failed execution is recorded below; it does not establish an improvement. The generated proposal has not been replaced or revised by hand. Subsequent repetitions have not started.

See [planning measurements](planning-result.json), [review findings](proposal-review.json), [approval boundary](review-status.json) and the retained planning log. Private requests, responses, full plans and observations remain in encrypted storage. The general 25% improvement gate remains unproven.

## Explicitly approved diagnostic execution and follow-up

The user explicitly approved the five requirements, the conditional consent click and diagnostic execution of revision 2/hash `180bd0c81ef4dee79c253bb6911277d44b78c9d9e275eddfff84016c5f51bc9f`. The unchanged first proposal was approved at revision 3 and executed once from frozen source `00b1aa751bbdd8787cda25f425a56db2e6850f51`.

The first extraction failed with `LLM_BUDGET_EXCEEDED`: the complete serialized request measured **951,251 conservative estimated input tokens**, above 96,000, with `dispatch_status: not_started`. The newly captured snapshot contained 1,580 records on 56 pages (1,155,527 compact UTF-8 bytes). The prompt-only inspection estimate was 738,636; these measurements have different scopes and must not be conflated. No extraction provider request, consent click, product visit or workbook write occurred. The earlier URL interpretation used one physical call, 114 input/141 output tokens and EUR 0.004293. Workflow cleanup returned `closed: true`; the independent post-cleanup current-page probe found no usable page, and its own cleanup also returned `closed: true`.

The execution oracle failed (`workflow_execution_failed`, `workbook_missing`); first-proposal business correctness is **false**. Execution took 23,291 ms. Total known run cost, including planning, is EUR 0.281585. Campaign cost plus unchanged unknown reservations is EUR 157.463077 of EUR 200. See [sanitized execution evidence](execution-result.json). The run is terminal and will not be replayed; later repetitions were not started.

The failure confirms that the first guidance change was insufficient. A new regression first failed because the prompt omitted the concrete learning boundary. The generic follow-up explains that extraction without `each` submits all bound inputs, including for a boolean or bounded result; declared fields can be projected with existing bindings and `foreach` exports without inference; pure projection tasks can provide that view. Shared inputs count toward learning admission, including any repeated full collection. Exact copying and row assembly use bindings. The first longer wording also exceeded a retained 24,000-token planning fixture; wording was consolidated while retaining its contracts and limit. No site-specific recipe, operator, admission increase or automatic plan rewrite is added. This corrects missing guidance; whether a fresh model follows it remains a separate live gate.

The work is now on `fix/taskplan-bounded-observations`, targeting `fix/planner-stabilization` in [PR #119](https://github.com/GnouGo/GnouGo/pull/119). Commit `6a81eb2` contains the initial reviewed implementation. Follow-up findings and validation are retained separately from the frozen live source.

Follow-up component validation: 1,619 of 1,620 Planning cases passed in the first run; the sole failure expected obsolete prompt wording. After updating that assertion, all 25 targeted cases passed. The mandatory 24,000-token context regression passed with every required contract retained. A new serial solution run is in progress; final validation will be appended without replacing these logs. See the [follow-up log manifest](followup-log-manifest.json).
