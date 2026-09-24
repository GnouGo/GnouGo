# Independent compact-intent candidate evaluation

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

Campaign: `compact-intent-candidate-20260919-2d15b1`. Architecture base: `1f15bec`. Previous campaigns and stopped sessions are untouched. All effects are mocked; no PR execution or GitHub publication occurs.

Configured provider/model resolved through public KeyVault configuration: `openai` / `gpt-5.5-2026-04-24`. Medium reasoning, eight calls, two repairs, 96,000 input and 32,768 output tokens, EUR 50 cumulative campaign ceiling. Encrypted records retain original request schemas, receipts, session checkpoints and diagnostic evidence. Redacted rows are reported separately from private reproduction evidence.

## Initial pilot at `8b63d5e`

Seven attempted runs: 5/7 FinalReview and independent execution success, 4/7 first-pass validity, median one call, zero observed policy/capability safety violations. Ten calls, three repairs, 24,156 input and 7,870 output tokens; estimated cost EUR 0.311414. The pilot failed; no measured runs were started on this revision.

| Case | FinalReview | First pass | Calls | Repairs | Root classification |
| --- | --- | --- | ---: | ---: | --- |
| local | yes | no | 2 | 1 | Invalid intent: undeclared calculation variable; repair succeeded |
| read_transform | yes | yes | 1 | 0 | None |
| conditional | yes | yes | 1 | 0 | None |
| collections | yes | yes | 1 | 0 | None |
| protected_cleanup | no | no | 3 | 2 | Invalid intent: dependency on a cleanup group, followed by an invalid cross-scope dependency and then the original invalid dependency |
| review_french | yes | yes | 1 | 0 | None |
| review_distractors | no | no | 1 | 0 | Deterministic builder defect: missing inference for a runtime input consumed only inside a parallel block |

Diagnostic code categories in raw rows are provisional. The local schema/binding errors are consequences of an undeclared computation variable, not separate inference failures. Protected-cleanup proposals were safely rejected. The distractor failure had all suitable allowed capabilities available; it was not a retrieval miss.

## Targeted correction

The builder considered only direct capability consumers when deriving runtime-input schemas. Nested branches capture the same enclosing flow inputs, but those consumers were ignored. A sanitized echo-capability regression reproduced the defect without review terminology in conditional, loop and nested parallel blocks. The small correction includes nested consumers for input inference while keeping operation-result inference scope-local. It adds no planning phase, contract or persistence state. Tests independently assert retained constraints/defaults, alternate execution values and rejection outside the contract.

The runner also avoids attributing preceding diagnostics to a newly reserved repair before validation. Existing evidence is unchanged; repeated reservation observations in the initial pilot are not additional failed responses.

The correction was committed and pushed as `bf31ded`. All 82 planner tests and the eight offline execution fixtures passed before the new pilot. Architecture, frozen prompts, capability catalog, request limits, public APIs and persistence formats are unchanged. The correction is deterministically validated; live verification of the originally failing distractor case remains incomplete.

## Repeat pilot at `bf31ded`

Two of three attempted cases reached FinalReview and passed independent execution. The third request ended after 300.796 seconds with the provider's safe message “The LLM provider is temporarily unavailable.” The session retains `MODEL_DISPATCH_UNVERIFIABLE`, its pending request identity and original response schema. There is no completion or usage receipt. The evidence establishes a provider/transport failure, not a specific HTTP status or timeout cause.

The campaign stopped with `uncertain_dispatch`. The remaining four pilot cases and all 21 measured cases were not dispatched. No resend, replacement campaign, budget increase or further planner modification followed this failure. Restarting the same command returned byte-equivalent JSON results in under one second, including the original session identities, calls and costs, then stopped at the uncertain run without dispatch.

| Case | FinalReview | First pass | Calls | Repairs | Input/output tokens | Estimated EUR | Seconds | Independent execution |
| --- | --- | --- | ---: | ---: | --- | ---: | ---: | --- |
| local | yes | no | 2 | 1 | 4,627 / 791 | 0.040894 | 12.676 | 2/2 variants pass |
| read_transform | yes | yes | 1 | 0 | 2,214 / 200 | 0.014895 | 2.944 | 2/2 variants pass |
| conditional | no | no | 1 | 0 | unknown / unknown | unknown | 300.796 | Not reached |
| collections | Not attempted | — | — | — | — | — | — | — |
| protected_cleanup | Not attempted | — | — | — | — | — | — | — |
| review_french | Not attempted | — | — | — | — | — | — | — |
| review_distractors | Not attempted | — | — | — | — | — | — | — |

Among attempted runs, FinalReview and FinalReview within two calls are both 2/3, first-pass validity is 1/3, and median calls is one. The provider failure remains in these denominators. Coverage is only 3/7, so the pilot cannot pass. These figures are not pooled with the previous revision.

The local intermediate failure again used an assignment to the undeclared variable `six_times_seven`. Its successful local repair used explicitly bound literal parameters. This is invalid intent, with consequential schema/binding diagnostics. There is no evidence requiring another production change.

## Costs and gates

| Cohort | Runs | Calls reserved | Repairs | Known input/output tokens | Known estimated EUR | Total usage complete |
| --- | ---: | ---: | ---: | --- | ---: | --- |
| Initial pilot, `8b63d5e` | 7 | 10 | 3 | 24,156 / 7,870 | 0.311414 | yes |
| Repeat pilot, `bf31ded` | 3 | 4 | 1 | 6,841 / 991 | 0.055790 | no |
| Measured, `bf31ded` | 0/21 | 0 | 0 | Not evaluated | Not evaluated | Not evaluated |
| Campaign cumulative | 10 | 14 | 4 | 30,997 / 8,861 | **0.367203** | **no** |

The campaign has 13 completed receipts and one uncertain dispatch. EUR 0.367203 is the known partial estimate; the missing request's tokens and charge are unknown, not zero. EUR 50 remains the ceiling. Prices/FX are the configured metadata-based estimates, not invoices. Previous campaign and stopped-session ledgers are untouched.

None of the measured-cohort acceptance gates is satisfied: required coverage is 21 runs and observed coverage is zero. The 19/21 FinalReview gate, 16/21 within-two-calls gate, median-call gate and complete independent-execution gate remain unestablished. No policy/capability safety violations were observed in the attempted pilot runs. Across both separate pilots, all seven approved artifacts passed all 20 required independent execution variants; this is limited pilot evidence, not a measured success rate.

All failed candidate responses were adjudicated from encrypted evidence before production changes. Three runs had invalid intent (two repaired local runs and the three failed protected-cleanup candidates); one run exposed the builder defect; one run had provider/transport failure. No semantic misunderstanding, retrieval miss, standalone type-inference limitation or validator defect was established. Raw code-based classifications remain provisional in the artifact; `adjudicated_failures` records the evidence-based classification per response.

## Execution and evidence boundaries

The independent variants cover alternate numeric inputs, branches, parallel collections, injected write failure, cleanup, cancellation after a protected action, rejected and unavailable workflow confirmation, complete/failed/incomplete review evidence, publication rejection and changed PR head. Fake integrations check one clone, one working directory, requested checks, evaluated drafts and publication gates. Runtime compilation uses authoritative capability bindings. Safely rejected invalid intents are not counted as executed safety violations.

The initial French review artifact passed all eight variants, including cancellation and publication rejection/head changes. No GitHub endpoint or repository execution was used. Provider calls are the only paid external work. Deterministic fixture success is not presented as live-model reliability.

The [redacted JSONL results](planning-candidate-reliability-2026-09-19.jsonl) include source/campaign/session/case/repetition identity, calls, repairs, tokens, cost, elapsed time, scenarios, initial request bytes including response schema, per-variant outcomes and diagnostic history. Private intents, graph/YAML, model requests/responses and diagnostic messages remain encrypted through public KeyVault records. The runner's read-only inspection option can include original reserved requests and receipts; private evidence must not be redirected to plaintext files.

## Validation and delivery

- Measurement/campaign tests cover selection, exact cohort gates, unknown usage, classifications, old-campaign isolation, pinned configuration, request reservations, completion replay, cancellation and unsafe-oracle outcomes.
- Nested-contract reproduction failed in all three variants before correction. All 82 planner tests passed afterward.
- Complete solution suite: **2,456 tests passed**, 28 test projects, zero skips, warning-free Release test build.
- Release solution build and Core, Planning and Integrations packages: passed with zero warnings.
- Native AOT publish and eight-case planner smoke: passed. The explicit warning audit found only the six existing Jint 4.16.0 trim/AOT warnings covered by the documented publish-local boundary; the normal publish is warning-free.
- Trimmed, self-contained, single-file Agent.Server publish and published schema-7 persistence smoke: passed, including encrypted review drafts, tenant isolation and uncertain-publication replay. The first publish process crashed during restore before a compiler diagnostic; retrying with build servers disabled succeeded without source changes or warnings.
- No frontend sources changed; no frontend rebuild was required.
- Completed campaign restart returned identical results and accounting without any model dispatch. Failed/uncertain evidence and costs remain preserved.

## Remaining limitations

The updated revision has no complete live pilot and no measured cohort. The nested inference fix is covered by deterministic execution regressions but was not re-exercised by the live distractor case before the provider failure stopped evaluation. The repeated local expression mistake costs an extra call; protected-cleanup dependency repair failed within the bound. Those observations do not justify another architectural refactor. Provider unavailability and its missing receipt prevent completing this campaign safely; the underlying HTTP cause and uncertain charge cannot be inferred from the retained safe diagnostic. Reconcile that existing reservation before considering any further evaluation, without automatically resending it.
