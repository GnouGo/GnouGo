# Input-default corrections and live validation

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

The input-default construction and repair defects are corrected. The latest pilot reached **6/7 FinalReview**, with **27/27 independent execution variants passing** across those six artifacts and zero observed safety violations. `collections` exhausted two repairs because the model alternated between explicit null and a missing default despite receiving the correct declaration targets. The pilot failed, so no 21-run measured cohort was started. Reliability acceptance gates remain unestablished on this revision.

All workflow integrations were mocked. No actual PR clone, repository execution or GitHub publication occurred. The planner architecture, public contract shapes, schema-7 storage, policy gates and cumulative budgets remain unchanged.

## Changes and deterministic evidence

- `fae41ef` restricts input-default hole domains, including nested objects and arrays, to contract-valid literals. Runtime bindings and omission candidates cannot become defaults. Exact input/output locations match declaration and member names, including reordered ports, subflows and confirmation wrappers. Graph assignment, intent reflection and rebuilding now commit atomically. Host reflection defects stop without spending a model repair.
- The same change routes non-enumerable missing defaults through ordinary input-declaration repair. Unresolved defaults no longer crash fixture sampling. Absent defaults, explicit nulls and literal defaults remain distinct; required runtime inputs do not require planning-time clarification. Interpretation guidance states that JSON `default: null` means no default, whereas `default: {"kind":"null"}` is an explicit null value.
- `0ba0786` follows a classified live failure reproduced for four primitive/container contracts and a nested subflow. Literal defaults are checked against their declared contracts before scenario generation. Invalid literals receive exact `INPUT_DEFAULT_INVALID` declaration diagnostics, instead of a misleading fixture request followed by an unrelated operation repair. No model output is silently corrected.

The first two sanitized reproductions failed before `fae41ef`; the five additional literal-contract reproductions failed before `0ba0786`. There are 21 new regression cases covering zero/one/multiple choices, catalog constants/defaults, invalid IDs, nested values, declaration/member reordering, subflows, confirmation insertion, failed-batch rollback, restart counters, bounded repair and null-versus-absence semantics. Existing substantive execution and safety tests remain in place. No new phase, persisted mapping or candidate-domain state was added. Flow.Core runtime, existing stored graphs/YAML/approvals and original request schemas were not changed.

## Campaign and cohorts

The existing encrypted campaign is `compact-intent-http-recovery-20260919-952f64a`. It retains its EUR 50 aggregate ceiling, pinned `openai` / `gpt-5.5-2026-04-24` configuration, medium reasoning, eight physical HTTP attempts per session, two repairs, 96,000 input and 32,768 output tokens and the existing timeouts/retry policy. The seven frozen requests ran unchanged and in order on each revision.

First-pass validity requires construction, compilation and scenarios without an additional model call. Calls below include physical transport attempts; intent repairs are counted separately. Independent execution is a separate outcome. Token and cost columns show verified **known portions**; unknown usage is never zero. Costs are metadata/FX estimates, not invoices. Elapsed time sums recorded run durations. Revisions and pilot/measured cohorts are not pooled.

| Revision / pilot | FinalReview | First-pass | Within 2 calls | Calls / repairs | Known input / output tokens | Known EUR | Seconds | Independent variants |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `fae41ef` | 3/7 | 3/7 | 3/7 | 15 / 8 | 43,795 / 13,927 | 0.55565881 | 160.214 | 9/9 |
| `0ba0786` | 6/7 | 4/7 | 5/7 | 12 / 4 | 27,959 / 10,826 | 0.40538831 | 417.663 | 27/27 |

The `fae41ef` pilot failed with four invalid-default proposals misrouted by validation. No measured cohort followed. After the targeted validator correction, the `0ba0786` pilot reached FinalReview in **85.71%**, first-pass validity in **57.14%**, and FinalReview within two physical calls in **71.43%**. Median calls were **1**, upper-quartile calls **3**. The complex subset (`collections`, `protected_cleanup`, `review_french`, `review_distractors`) had median **2**, upper quartile **3**.

`review_french` passed on its first call. `review_distractors` needed one declaration repair and one automatic recovery from an uncertain transport attempt: two logical model requests, three physical attempts. Both review artifacts passed all eight independent variants: nominal, failed checks, incomplete verification, publication rejection, changed head, cancellation, workflow approval denial and unavailable permission. The mocks verify one clone/work directory, evidence-backed check outcomes, cleanup and publication gates.

### Acceptance gates

| Gate | Current outcome |
| --- | --- |
| Clean pilot: all seven FinalReview and correct execution | Failed: 6/7; all six artifacts passed every required variant |
| Measured FinalReview at least 19/21 | Not evaluated; measured cohort correctly withheld |
| Measured FinalReview within two calls at least 16/21 | Not evaluated |
| Measured median calls at most two | Not evaluated; pilot median is 1 |
| Zero policy/capability safety violations | Zero observed across both pilots |
| Every artifact reaching review passes independent execution | Passed in both pilots: 3/3 and 6/6 artifacts |
| Overall measured reliability | Not established; incomplete measured coverage cannot pass |

## Confirmed classifications

Runner categories are provisional. These classifications use encrypted responses, issued repair targets, diagnostics and the frozen requests. Raw rows remain intact in the accompanying JSONL; confirmed classifications are additional records.

1. **Deterministic builder defect, fixed by `fae41ef`:** the previously recorded boolean/string missing-default cases exposed runtime-binding candidates and failed intent reflection. Sanitized regressions reproduced both. Read-only replay now produces located default diagnostics rather than partial assignment or host exceptions.
2. **Invalid intent plus validator defect, `fae41ef` pilot:** `conditional`, `collections`, `review_french` and `review_distractors` supplied explicit null defaults for non-nullable inputs. Validation incorrectly requested a fixture first, then offered only `/operations` for the runtime schema errors. Neither target could correct those declarations. The defect was classified and reproduced before `0ba0786`. Each run consumed two repairs and stopped safely. `collections`' second repair additionally renamed the producer without updating its output reference (invalid intent); `review_french` replaced runtime review instructions with hardcoded text (semantic misunderstanding) and left an input contract unresolved. These were consequences of unsuccessful repairs, not approved executions or independent proof of an inference defect.
3. **Invalid intent, repaired, `0ba0786` pilot `conditional`:** a null boolean default received the exact input target. The model replaced it with JSON `default: null`; FinalReview and both independent variants passed in two calls, one repair.
4. **Invalid intent, exhausted, `0ba0786` pilot `collections`:** the original main array input and subflow number input had explicit null defaults. Repair 1 changed both to `kind=missing`; repair 2 changed both back to `kind=null`. Both repairs received the correct `/inputs/0` and `/subflows/0/inputs/0` targets with literal/default guidance. Deterministic validation rejected every proposal. The provisional `HOLE_UNRESOLVED` inference label does not indicate an inference limitation here: the contracts were concrete and the missing default was the invalid field. No additional production fix or prompt-specific workaround was made.
5. **Invalid intent, repaired, plus provider/transport recovery, `0ba0786` pilot `review_distractors`:** null string defaults were corrected to absent defaults on the issued input targets. The repair receipt reports two transport attempts, one uncertain attempt and a retained conservative usage allowance. The bounded shared HTTP retry recovered under a new attempt identity. Exact provider-side usage for the uncertain attempt remains unknown; this report does not infer a specific network cause from the missing receipt.

No capability retrieval miss or demonstrated type inference limitation occurred in these pilots. No new architectural defect was established. The remaining model limitation is failure to consistently distinguish absent defaults from explicit null/missing defaults, even during a correctly targeted bounded repair.

## Read-only replay, restart and accounting

The two failed `77aa37d` interpretations were replayed with their original schemas before paid evaluation. The four failed `fae41ef` interpretations were replayed after literal validation was corrected. All six replays made **zero live calls** and left campaign evidence unchanged. Replay intentionally has only the original interpretation receipt, so invalid defaults remain diagnostics; mocked correction regressions establish repair success. Replays are not counted as new live successes.

The `fae41ef` replays now report only `INPUT_DEFAULT_INVALID`, with no fixture detour. Their before/after evidence hash was `ec88eb4dafdceff5fcd9334ec30e16b378cb52003c1c3645f6edbfe5c3ee1553`. The earlier missing-default replay kept hash `bb4b1dca62195a5ac6b7d8f7349db9d332f8fe6ec71fe187257658d814eb2483` unchanged.

Restarting the completed `0ba0786` pilot returned byte-identical results, exit 1 for the failed gate, and unchanged evidence/accounting. It dispatched no additional requests and added no charges, including for the previously recovered uncertain attempt. Final evidence hash: `4cd8dc1691a2413023a190b0a965dbad6e8cb9eeda039d7c8363da08970a314d`.

The campaign now holds **72 logical reservations and 72 completion receipts, 74 physical attempts**. Verified portions total **188,977 input tokens, 61,659 output tokens and EUR 2.43861693**. One recovered uncertain attempt retains allowances of **96,000 input tokens, 32,768 output tokens and EUR 1.27664921**. Actual total usage/cost remains unknown; the conservative campaign cost upper bound is **EUR 3.71526614**, below EUR 50. A logical completion receipt does not erase an earlier uncertain physical attempt.

This task added 14 live runs, 27 physical attempts and **EUR 0.96104712 of known cost**, plus that unknown allowance. The previous EUR 1.47756981 remains included.

The separate older campaign `compact-intent-candidate-20260919-2d15b1` remains untouched: 14 reservations, 13 receipts, one uncertain pre-journal request and known EUR 0.36720332 plus unknown usage. Its unchanged evidence hash is `61bb530740935a8a98a963504326f8a196f9116b5bc8cf71cf7b0333c4409db7`. No old request was resent or assigned zero usage.

## Final verification

Verification on `0ba0786`, .NET SDK 10.0.300, macOS arm64:

| Check | Result |
| --- | --- |
| Planner tests | 128 passed, including 21 new default/repair regressions |
| Campaign tests | 14 passed |
| Complete Release solution suite | 2,547 passed, zero failed, one opt-in live GitHub test skipped; 29 projects |
| Release solution and benchmark builds | Passed without warnings/errors |
| Flow.Core and Flow.Planning packages | Created without warnings/errors |
| Eight frozen offline cases | All passed |
| Native AOT planner publish and eight-case smoke | Passed without warnings |
| Trimmed self-contained single-file Agent publish | Passed without warnings |
| Published Agent persistence smoke | Schema-7 encrypted persistence, tenant isolation and uncertain-publication replay passed |
| Restart/replay accounting | Byte-identical restart results; unchanged receipts, evidence hash and cumulative accounting |

Normal solution builds ran the required frontend targets. No frontend sources or warning suppressions changed. The Agent publish used an isolated archive of the committed revision and an isolated persistence workspace. Source fixes were tested, committed and pushed before their respective live pilots. Final report changes contain only redacted observations; private reproduction evidence remains encrypted through public KeyVault records.

## Per-run pilot results

### `fae41ef`

| Case | FinalReview | First-pass | Calls | Repairs | Known input / output tokens | Known EUR | Seconds | Independent execution |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `local` | Yes | Yes | 1 | 0 | 2,357 / 147 | 0.01413176 | 3.998 | 2/2 |
| `read_transform` | Yes | Yes | 1 | 0 | 2,361 / 183 | 0.01509162 | 2.413 | 2/2 |
| `conditional` | No | No | 3 | 2 | 5,838 / 2,863 | 0.10041885 | 38.751 | Not run |
| `collections` | No | No | 3 | 2 | 6,068 / 2,837 | 0.10074171 | 37.363 | Not run |
| `protected_cleanup` | Yes | Yes | 1 | 0 | 2,366 / 315 | 0.01856894 | 3.529 | 5/5 |
| `review_french` | No | No | 3 | 2 | 10,557 / 3,590 | 0.14003927 | 33.064 | Not run |
| `review_distractors` | No | No | 3 | 2 | 14,248 / 3,992 | 0.16666667 | 41.096 | Not run |

### `0ba0786`

| Case | FinalReview | First-pass | Calls | Repairs | Known input / output tokens | Known EUR | Seconds | Independent execution |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `local` | Yes | Yes | 1 | 0 | 2,357 / 147 | 0.01413176 | 3.888 | 2/2 |
| `read_transform` | Yes | Yes | 1 | 0 | 2,361 / 182 | 0.01506545 | 2.407 | 2/2 |
| `conditional` | Yes | No | 2 | 1 | 3,790 / 429 | 0.02776614 | 6.995 | 2/2 |
| `collections` | No | No | 3 | 2 | 5,989 / 6,104 | 0.18592059 | 62.081 | Not run |
| `protected_cleanup` | Yes | Yes | 1 | 0 | 2,366 / 262 | 0.01718150 | 3.195 | 5/5 |
| `review_french` | Yes | Yes | 1 | 0 | 3,091 / 1,717 | 0.05843368 | 17.370 | 8/8 |
| `review_distractors` | Yes | No | 3 | 1 | 8,005 / 1,985 + unknown | 0.08688918 + unknown | 321.727 | 8/8 |
