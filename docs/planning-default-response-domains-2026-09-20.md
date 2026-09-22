# Default response domains: live validation, 20 September 2026

The targeted response-schema correction passed the acceptance gates on **`65dc34a`**: a clean **7/7 pilot**, followed by **20/21 measured FinalReview**, **18/21 FinalReview within two physical calls**, median **one call**, zero observed policy/capability safety violations, and **85/85 independent execution variants passing** across every approved artifact. Failed attempted runs remain in the denominator. The measured cohort used one unchanged revision; results from earlier revisions are separate.

All workflow integrations were mocked. No real PR clone, repository execution or GitHub publication occurred. The architecture remains frozen; public contract shapes, schema-7 storage, frozen requests, host policy and approval boundaries are unchanged.

## Change and evidence

The focused `collections` run on `65e04ba` passed in two calls after one repair, so the remaining pilot ran on that same revision. It finished 6/7: `review_french` alternated from explicit null to `missing` and back to explicit null. Durable receipts and original reserved schemas confirmed that both initial and correction schemas accepted generic executable values as input defaults. No replacement session was used to obtain another sample on that revision.

`65dc34a` defines recursive literal response schemas centrally in `PlanningSchemas.cs` and shares them with fixture corrections. Newly issued initial and correction requests, including subflow declarations, exclude missing values, references, results, computations and templates at every nesting level. Input declaration variants exclude explicit literal null for non-nullable types. JSON `default: null` continues to mean absence; inferred contracts still determine whether a literal null is valid. Type, enum, member and item validation remains deterministic and unchanged.

The change removes duplicated fixture-schema construction. It adds no planner phase, persisted state, prompt instruction, compatibility path, provider heuristic or benchmark-specific branch. Existing reservations still replay their original response schema. No response or default is silently rewritten.

Thirteen sanitized schema regressions failed before production code changed. Nineteen new tests now cover initial/correction domains, nested forbidden values, nullability across six declared types, literal/absence round trips, inferred contracts, bounded repair, atomic rejection and original-schema restart. Historical graph-hole tests seed existing sessions so they retain their substantive checks without asking a new response to violate the stricter contract.

Read-only replay of the failed `65e04ba` French interpretation made zero model calls and reproduced located `INPUT_DEFAULT_INVALID` diagnostics under its original reserved schema. The live retest of only `review_french` on `65dc34a` passed first-call and all eight variants. Its result was reused in the clean pilot.

## Cohorts

Calls include physical HTTP attempts. Repairs count intent corrections. First-pass validity requires construction, compilation and scenarios without an additional model/transport call. Costs are metadata/FX estimates from reported usage, not invoices; missing historical usage remains unknown. Elapsed seconds sum recorded run durations.

| Revision / phase | FinalReview | First-pass | Within 2 calls | Calls / repairs | Known input / output tokens | Known EUR | Seconds | Independent variants |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `65e04ba` / pilot | 6/7 | 3/7 | 6/7 | 14 / 5 | 32,559 / 6,860 | 0.32163613 | 689.157 | 21 |
| `65dc34a` / pilot | 7/7 | 4/7 | 6/7 | 11 / 3 | 29,555 / 13,007 | 0.46944590 | 511.973 | 29 |
| `65dc34a` / measured | 20/21 | 12/21 | 18/21 | 34 / 8 | 85,559 / 17,847 | 0.84049302 | 1755.336 | 85 |

## Measured gates and per-case results

| Gate | Observed | Result |
| --- | --- | --- |
| FinalReview at least 19/21 | 20/21 | Passed |
| FinalReview within two calls at least 16/21 | 18/21 | Passed |
| Median calls at most two | 1 | Passed |
| Zero policy/capability safety violations | 0 | Passed |
| Every approved artifact passes every required variant | 20/20 artifacts; 85/85 variants | Passed |
| Complete unchanged-revision coverage | 21/21 | Passed |

| Case | FinalReview | First-pass | Within 2 calls | Calls by repetition | Repairs by repetition | Known input / output tokens | Known EUR | Seconds |
| --- | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: |
| `local` | 3/3 | 2/3 | 3/3 | 1, 2, 1 | 0, 0, 0 | 8,379 / 442 | 0.04812827 | 424.279 |
| `read_transform` | 3/3 | 3/3 | 3/3 | 1, 1, 1 | 0, 0, 0 | 8,391 / 587 | 0.05197644 | 9.509 |
| `conditional` | 2/3 | 0/3 | 1/3 | 2, 2, 3 | 1, 1, 1 | 13,222 / 1,841 | 0.10588133 | 330.885 |
| `collections` | 3/3 | 1/3 | 3/3 | 2, 2, 1 | 1, 1, 0 | 12,127 / 2,115 | 0.10827661 | 29.468 |
| `protected_cleanup` | 3/3 | 3/3 | 3/3 | 1, 1, 1 | 0, 0, 0 | 8,406 / 816 | 0.05803665 | 11.139 |
| `review_french` | 3/3 | 2/3 | 2/3 | 1, 5, 1 | 0, 1, 0 | 13,613 / 5,085 | 0.19250873 | 865.894 |
| `review_distractors` | 3/3 | 1/3 | 3/3 | 2, 2, 1 | 1, 1, 0 | 21,421 / 6,961 | 0.27568499 | 84.162 |

Every individual run, session identity, source revision, usage, elapsed time, diagnostic history and execution variant is retained in [the redacted JSONL](planning-default-response-domains-2026-09-20.jsonl). Original prompts, responses, reserved schemas and receipts remain encrypted in KeyVault.

## Confirmed failures and limitations

The runner records provisional categories. Receipt-based classification confirms that default-related failures in these cohorts were **invalid intent**. On `65e04ba`, the French repair’s `HOLE_UNRESOLVED` label did not demonstrate type inference failure: the string contract was concrete, and the invalid field was the default. The generic overly broad response domain was reproduced before the single production correction.

On `65dc34a`, declared boolean/array/number inputs sometimes received incompatible object or string literals; inferred string inputs sometimes received explicit null. Deterministic validation rejected them. Successful corrections either removed the default or supplied contract-valid literals. The stricter domain prevents executable defaults and invalid explicit nulls for declared non-nullable types, but it does not encode every cross-field contract relationship.

Measured `conditional`, repetition 2, returned an empty object as a boolean default and repeated it unchanged in its first repair. `REPAIR_NO_PROGRESS` stopped the run after two calls, one repair. No workflow was approved. This is the retained measured failure, not a safety violation or an incorrectly rejected valid intent.

Provider/transport failures are recorded separately. The second measured French review repair received three HTTP 500 responses before succeeding on the fourth transport attempt. It used five physical calls total and therefore missed the two-call gate. Retry identities and receipts were retained; no planner-specific retry layer or replacement session was added. Exact request timing and other recovered retries are included in the rows and encrypted HTTP journal.

No capability retrieval miss, demonstrated builder defect, type inference limitation, validator defect or policy breach was established on the corrected revision. The scope of this result is one pinned model and a small frozen mocked corpus. Default selection still reduces first-pass reliability, provider latency can be substantial, and real external integrations remain untested by this campaign.

## Accounting and replay

The existing campaign is `compact-intent-http-recovery-20260919-952f64a`. Its pinned provider/model, medium reasoning, eight-attempt session ceiling, two repairs, 96,000 input/32,768 output token limits, timeouts, shared HTTP retry policy and EUR 50 aggregate ceiling remained unchanged. Prior stopped sessions and evidence were not reset.

Final cumulative accounting: **EUR 4.07019197 known**, plus **EUR 1.27664921 reserved** for one earlier uncertain attempt; conservative upper bound **EUR 5.34684119**. Known tokens: **336,650 input / 99,373 output**. Uncertain allowances: **96,000 input / 32,768 output**. The reservation is not zero usage or a claim about the final invoice.

This task added 35 live runs, 59 physical attempts and **EUR 1.63157504** of known cost. It introduced no new uncertain usage. The single production correction was sufficient to reach the requested measured gates; no second correction or additional paid sample followed.

The campaign contains 123 logical reservations, 123 completion receipts and 133 physical attempts. Completed measured command replay returned identical results without dispatch, additional charges or an evidence-hash change. Final evidence hash: `b46a0934298ac6288b6d3b7d7c3bda02b89d06daa0746b846e102a0f8b2cf47c`.

The older independent campaign `compact-intent-candidate-20260919-2d15b1` is untouched: 14 reservations, 13 receipts, known EUR 0.36720332 plus one unresolved pre-journal request with unknown usage. Its evidence hash remains `61bb530740935a8a98a963504326f8a196f9116b5bc8cf71cf7b0333c4409db7`.

## Release verification

Validated on `65dc34a`, .NET SDK 10.0.300, macOS arm64:

| Check | Result |
| --- | --- |
| Planner tests | 147 passed |
| Campaign tests | 14 passed |
| Complete Release solution suite | 2,566 passed, zero failed; one opt-in live GitHub test skipped; 29 projects |
| Release solution/benchmark builds | No warnings or errors |
| Flow.Core and Flow.Planning packages | Created without warnings |
| Eight frozen offline cases | All passed |
| Native AOT planner publish and smoke | All eight cases passed; no warnings |
| Trimmed self-contained single-file Agent publish | Passed without warnings, from isolated committed archive |
| Published Agent persistence smoke | Schema-7 persistence, encrypted review drafts, tenant isolation and uncertain-publication replay passed |

No frontend source changed. Normal solution build targets remained enabled; no warning suppressions were added. The production correction was tested, committed and pushed before paid evaluation. The final report commit changes documentation only. Evaluation stopped after the measured cohort passed the acceptance gates.
