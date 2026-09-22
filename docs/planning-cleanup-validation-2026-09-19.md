# Cleanup ordering correction and live validation

The cleanup ordering defect is corrected, including guarded conditions and cancellation testing. The full reliability gates remain unmet: the latest pilot exposed a separate input-default hole defect in two cases. Paid evaluation stopped after that failed pilot. No actual repository clone, PR execution or GitHub publication occurred.

## Changes and scope

Commit `71ebb4f` makes cleanup `after` edges express ordering without requiring predecessor success. The builder still derives availability guards from actual bindings, conditions and expressions. Structural validation retains dependency existence, scope and cycle checks without treating an ordering edge as evidence that a result exists. Interpretation instructions, repair hints and contract documentation describe the same rule.

New deterministic tests also exposed a pre-existing compiler defect: a combined availability guard and business condition evaluated the condition's named argument before testing availability. The compiler now short-circuits the host-generated guard before evaluating a resource-dependent condition. This is a small lowering correction, not a general change to calculation parameter semantics.

Schema-7, public contract shapes, budgets and the planning architecture are unchanged. Flow.Core execution behavior is unchanged; its only source change is documentation on the existing `After` property. No stored graph, YAML, approval or original model receipt was migrated or rewritten. Restart retains existing graphs. Explicit revisions rebuild and invalidate approval. Exact artifact verification continues to reject stored YAML that differs from current compilation, including obsolete compound-guard lowering; such artifacts need explicit revision and review.

Commit `77aa37d` corrects only benchmark cancellation injection. It interrupts the mock work operation before the response returns instead of setting cancellation after the final operation has already completed. The oracle still requires a failed/cancelled result, cleanup, no publication and no policy violations. Frozen requests and business expectations were not relaxed.

## Campaign and measurements

All live runs use the same encrypted campaign, `compact-intent-http-recovery-20260919-952f64a`, with the original EUR 50 ceiling. Configuration remains pinned to `openai` / `gpt-5.5-2026-04-24`, medium reasoning, eight physical attempts per session, two intent repairs, 96,000 input and 32,768 output tokens, and the existing HTTP retry/time limits. Integrations are mocked. Previous revisions' rows and costs remain intact.

First-pass validity means construction, compilation and scenarios after one interpretation/physical model attempt. Calls include transport attempts and bounded choice calls; repairs count intent corrections. Independent execution is separate from reaching FinalReview. Costs are usage-and-metadata/FX estimates, not invoices. Elapsed time below is the sum of recorded run durations.

| Revision/cohort | Runs | FinalReview | First-pass | Calls | Repairs | Input / output tokens | EUR | Seconds | Independent variants |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `pilot-71ebb4f` | 7 | 7/7 | 7/7 | 7 | 0 | 18,399 / 5,044 | 0.21231675 | 60.792 | 29/29 |
| `measured-71ebb4f` | 21 | 19/21 | 19/21 | 24 | 2 | 59,880 / 19,627 | 0.77505236 | 239.958 | 76/77 |
| `pilot-77aa37d` | 7 | 5/7 | 5/7 | 8 | 0 | 20,496 / 7,042 | 0.27376963 | 82.705 | 19/19 |

The `71ebb4f` pilot passed all seven cases, permitting its complete 21-run measured cohort. That cohort reached FinalReview in **19/21 runs (90.48%)**, all within two calls. Median and upper-quartile calls were both **1**, across all measured runs and across the complex-case subset. Its recorded independent-execution gate failed on the cancellation oracle issue described below.

The corrected benchmark revision, `77aa37d`, then ran a fresh seven-case pilot within the same campaign. **5/7** reached FinalReview; all five passed every required execution variant. The two input-default failures prevented a measured cohort on that revision. Results from different revisions are not pooled, and offline replay does not replace failed live rows.

### Acceptance gates

| Gate | `71ebb4f` measured, 21 runs | `77aa37d` |
| --- | --- | --- |
| FinalReview at least 19/21 | Passed: 19/21 | No measured cohort |
| FinalReview within two calls at least 16/21 | Passed: 19/21 | No measured cohort |
| Median calls at most two | Passed: 1 | Pilot median 1; measured gate not established |
| Zero observed policy/capability safety violations | Passed | Zero observed in pilot; measured gate not established |
| Every approved workflow passes every required variant | Failed as recorded: 18/19 workflows, 76/77 variants | Pilot 5/5 workflows, 19/19 variants; measured gate not established |
| Overall | Failed as recorded | Pilot failed; measured coverage unavailable |

## Confirmed failure classifications

The JSONL runner labels are provisional. The following classifications follow inspection of original encrypted responses, exact diagnostics, code paths and oracle expectations. [Redacted rows and classifications](planning-cleanup-validation-2026-09-19.jsonl) preserve both original labels and these confirmed findings.

- **Invalid intent — `71ebb4f`, measured `conditional`, repetition 1.** The initial proposal bound an absent conditional-read result into an unconditional calculation. Repair 1 guarded the calculation but left its output unconditional; repair 2 moved the absent read binding into the output expression. A ternary inside a calculation does not make its named input values available. All three responses remained invalid, both repair attempts were consumed, and validation prevented review/execution. The request is expressible using the existing `choose` operation, as other independent runs demonstrate.
- **Deterministic construction defect — input-default holes.** `71ebb4f` measured `review_french` repetition 1, then `77aa37d` pilot `conditional` and `review_french`, supplied `default: {"kind":"missing"}` on required runtime inputs. Hole eligibility exposed runtime-data bindings even though defaults require literals. Choice reflection mapped the hole to the enclosing input declaration and failed with “A resolved business choice has no editable intent value.” The subsequently assigned nonliteral graph default failed literal compilation. This recurred independently for boolean and string inputs. No workflow reached review/execution, and no intent repair was issued. The benchmark's 40-advance bound ended these runs; this was not budget exhaustion or a provider failure. The provisional `invalid_intent` category alone understates this host defect.
- **Validator/oracle defect — `71ebb4f`, measured `protected_cleanup`, repetition 3.** The generated intent correctly contained one write, unconditional cleanup and a literal output 42. The mock cancelled after the write had completed; there was no subsequent main operation to observe cancellation. Cleanup ran exactly once and the workflow legitimately completed before observing the cancellation request. The oracle demanded a cancelled result and flagged a mismatch. The mock now throws caller cancellation before returning the work response, preserving the strict expected outcome. A sanitized deterministic reproduction covers last-step execution, nested subflows and PR-review work interruption.

No capability retrieval miss, type inference limitation or provider/transport failure occurred in these new cohorts. The older campaign's recovered HTTP 500 remains part of cumulative accounting and its earlier report; it is not counted as a new failure here.

The remaining production limitation is input-default hole handling and reflection. A targeted follow-up should restrict default candidates to valid literals, map default fields precisely and route non-enumerable defaults into ordinary bounded declaration repair. It must preserve absent defaults versus explicit null, avoid requesting planning-time values for declared runtime inputs, and add a sanitized deterministic reproduction before changing production code. This report makes no claim that this separate issue has been fixed.

## Replay, accounting and isolation

The original `c0a49f4:pilot:protected_cleanup:1` receipt now passes all five independent variants under the corrected construction rules. The `71ebb4f:measured:protected_cleanup:3` receipt passes all five under the corrected cancellation injection. Both read-only replays used the original request schemas, made zero model calls and left the campaign evidence hash unchanged. They are diagnostic evidence, not new live successes. All 19 approved artifacts from the measured cohort were also replayed against the corrected cancellation test: **19/19 artifacts and 77/77 variants passed**, with zero model calls and an unchanged campaign evidence hash. This retrospective check supports the oracle diagnosis; it neither overwrites the original cohort nor supplies the missing measured cohort on the corrected benchmark revision.

Restarting the `77aa37d` pilot command returned byte-identical rows and accounting without new dispatch or charges. Exit 1 correctly reflected the failed pilot gate.

The campaign now contains **46 model reservations and 46 completion receipts, 47 physical HTTP attempts**, 117,223 input tokens and 36,906 output tokens. Cumulative known estimate: **EUR 1.47756981**. This cleanup task added 35 live runs, 39 physical attempts and **EUR 1.26113874**; the previous EUR 0.21643106 remains included. No outstanding unknown-usage allowance remains in this campaign. Evidence hash after evaluation and read-only replays: `bb4b1dca62195a5ac6b7d8f7349db9d332f8fe6ec71fe187257658d814eb2483`.

The older `compact-intent-candidate-20260919-2d15b1` campaign remains unchanged: 14 reservations, 13 completion receipts and one uncertain request. Its known estimate remains EUR 0.3672033158813263525305410123 plus unknown usage. Its evidence hash is still `61bb530740935a8a98a963504326f8a196f9116b5bc8cf71cf7b0333c4409db7`. No old uncertainty was resent or assigned zero usage.

## Verification and delivery

Both code commits were tested and pushed to `feat/deterministic-planner-v2` before their live runs. Final verification on `77aa37d`, .NET SDK 10.0.300, macOS arm64:

| Check | Outcome |
| --- | --- |
| Planning component tests | 107 passed |
| Campaign persistence/accounting tests after production correction | 14 passed; also covered by the final solution suite |
| Complete Release solution suite | 2,526 passed, zero failed, one opt-in live GitHub test skipped; 29 projects |
| Release solution and benchmark builds | Passed without warnings/errors |
| Flow.Core and Flow.Planning package creation | Passed without warnings/errors |
| Eight offline benchmark cases | Passed; independent from live-model scores |
| Native AOT planner publish and eight-case smoke | Passed without warnings |
| Trimmed self-contained single-file Agent publish | Passed without warnings |
| Published Agent persistence smoke | Schema-7 persistence, encrypted review drafts, tenant isolation and uncertain-publication replay passed |

Regression coverage includes success/failure/cancellation, skipped acquisition, explicit false conditions, resource-dependent conditions, group dependencies, sibling order, unknown dependencies/cycles/scopes, stripped-guard rejection, nested subflows, denied/unavailable confirmation, restart preserving stored graphs and fresh approval after explicit revision. The final Agent publish used an isolated archive and persistence workspace. Normal solution builds rebuilt required frontends; the isolated persistence publish skipped bundled tools/frontend rebuilding. No frontend sources, Flow.Core runtime behavior or warning suppressions changed.

## Per-run results

### pilot-71ebb4f

| Case | Repetition | FinalReview | First-pass | Calls | Repairs | Input / output tokens | EUR | Seconds | Execution |
| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| local | 1 | Yes | Yes | 1 | 0 | 2,326 / 148 | 0.01402269 | 3.775 | 2/2 |
| read_transform | 1 | Yes | Yes | 1 | 0 | 2,330 / 182 | 0.01493019 | 2.595 | 2/2 |
| conditional | 1 | Yes | Yes | 1 | 0 | 2,337 / 227 | 0.01613874 | 3.692 | 2/2 |
| collections | 1 | Yes | Yes | 1 | 0 | 2,350 / 921 | 0.03436300 | 12.688 | 2/2 |
| protected_cleanup | 1 | Yes | Yes | 1 | 0 | 2,335 / 270 | 0.01725567 | 3.153 | 5/5 |
| review_french | 1 | Yes | Yes | 1 | 0 | 3,060 / 1,685 | 0.05746073 | 16.690 | 8/8 |
| review_distractors | 1 | Yes | Yes | 1 | 0 | 3,661 / 1,611 | 0.05814572 | 18.199 | 8/8 |

### measured-71ebb4f

| Case | Repetition | FinalReview | First-pass | Calls | Repairs | Input / output tokens | EUR | Seconds | Execution |
| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| local | 1 | Yes | Yes | 1 | 0 | 2,326 / 292 | 0.01779232 | 5.815 | 2/2 |
| read_transform | 1 | Yes | Yes | 1 | 0 | 2,330 / 184 | 0.01498255 | 2.850 | 2/2 |
| conditional | 1 | No | No | 3 | 2 | 5,796 / 2,007 | 0.07782723 | 29.998 | Not reached |
| collections | 1 | Yes | Yes | 1 | 0 | 2,350 / 807 | 0.03137871 | 11.211 | 2/2 |
| protected_cleanup | 1 | Yes | Yes | 1 | 0 | 2,335 / 271 | 0.01728185 | 3.555 | 5/5 |
| review_french | 1 | No | No | 2 | 0 | 4,284 / 1,980 | 0.07052356 | 22.713 | Not reached |
| review_distractors | 1 | Yes | Yes | 1 | 0 | 3,661 / 1,649 | 0.05914049 | 17.047 | 8/8 |
| local | 2 | Yes | Yes | 1 | 0 | 2,326 / 148 | 0.01402269 | 2.373 | 2/2 |
| read_transform | 2 | Yes | Yes | 1 | 0 | 2,330 / 383 | 0.02019197 | 5.630 | 2/2 |
| conditional | 2 | Yes | Yes | 1 | 0 | 2,337 / 230 | 0.01621728 | 3.169 | 2/2 |
| collections | 2 | Yes | Yes | 1 | 0 | 2,350 / 909 | 0.03404887 | 12.698 | 2/2 |
| protected_cleanup | 2 | Yes | Yes | 1 | 0 | 2,335 / 273 | 0.01733421 | 3.488 | 5/5 |
| review_french | 2 | Yes | Yes | 1 | 0 | 3,060 / 2,619 | 0.08191099 | 24.260 | 8/8 |
| review_distractors | 2 | Yes | Yes | 1 | 0 | 3,661 / 1,742 | 0.06157504 | 17.578 | 8/8 |
| local | 3 | Yes | Yes | 1 | 0 | 2,326 / 147 | 0.01399651 | 2.689 | 2/2 |
| read_transform | 3 | Yes | Yes | 1 | 0 | 2,330 / 180 | 0.01487784 | 2.883 | 2/2 |
| conditional | 3 | Yes | Yes | 1 | 0 | 2,337 / 750 | 0.02982984 | 12.788 | 2/2 |
| collections | 3 | Yes | Yes | 1 | 0 | 2,350 / 922 | 0.03438918 | 12.904 | 2/2 |
| protected_cleanup | 3 | Yes | Yes | 1 | 0 | 2,335 / 610 | 0.02615620 | 9.653 | 4/5 |
| review_french | 3 | Yes | Yes | 1 | 0 | 3,060 / 1,796 | 0.06036649 | 19.451 | 8/8 |
| review_distractors | 3 | Yes | Yes | 1 | 0 | 3,661 / 1,728 | 0.06120855 | 17.205 | 8/8 |

### pilot-77aa37d

| Case | Repetition | FinalReview | First-pass | Calls | Repairs | Input / output tokens | EUR | Seconds | Execution |
| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| local | 1 | Yes | Yes | 1 | 0 | 2,326 / 148 | 0.01402269 | 3.780 | 2/2 |
| read_transform | 1 | Yes | Yes | 1 | 0 | 2,330 / 391 | 0.02040140 | 5.252 | 2/2 |
| conditional | 1 | No | No | 1 | 0 | 2,337 / 1,015 | 0.03676702 | 15.527 | Not reached |
| collections | 1 | Yes | Yes | 1 | 0 | 2,350 / 1,112 | 0.03936300 | 12.878 | 2/2 |
| protected_cleanup | 1 | Yes | Yes | 1 | 0 | 2,335 / 275 | 0.01738656 | 3.227 | 5/5 |
| review_french | 1 | No | No | 2 | 0 | 5,157 / 2,336 | 0.08365183 | 24.313 | Not reached |
| review_distractors | 1 | Yes | Yes | 1 | 0 | 3,661 / 1,765 | 0.06217714 | 17.728 | 8/8 |
