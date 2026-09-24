# Live validation of HTTP recovery

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

## Scope and configuration

Campaign `compact-intent-http-recovery-20260919-952f64a` is a distinct validation of the HTTP retry implementation, authorized by the request to run live tests. It retains an independent encrypted KeyVault ledger and a EUR 50 ceiling. Previous campaigns and stopped planning sessions remain untouched. All workflow effects are mocked: no repository was cloned, no GitHub review was published and no actual PR execution occurred.

The configured provider/model resolved through KeyVault was `openai` / `gpt-5.5-2026-04-24`. The campaign pinned medium reasoning, eight physical model attempts per session, two repairs, 96,000 input and 32,768 output tokens. Pinned HTTP policy: four total attempts, one uncertain retry, 600,000 ms per-attempt timeout, 1,000 ms initial exponential bound, 30,000 ms maximum backoff and 60,000 ms total delay allowance. There are no planner-specific transport retries.

Raw intent, YAML, responses and HTTP bodies remain encrypted. The accompanying [redacted results](planning-http-live-validation-2026-09-19.jsonl) retain revision/session identities, measurements, independent variants and classifications; no private response content or credentials are included.

## Initial preflight defect and correction

The initial run on `952f64a` stopped before any HTTP dispatch. Planner requests prefer background generation, while the new benchmark adapter rejected background requests to enforce synchronous recovery. This was a benchmark adapter defect, classified under provider/transport failure; it was not a model failure or uncertain provider dispatch. Public record inspection confirmed zero provider reservations, receipts and physical attempts. The planner had already counted one logical call, and its failed checkpoint and unknown-usage diagnostic remain intact.

Commit `c0a49f4` selects synchronous generation on the adapter's dispatched copy and preserves the original request, identity, limits and strict response schema. Tool-bearing requests still fail before dispatch. Fourteen campaign tests passed, including both new regression tests, and the benchmark built without warnings before commit/push. Production planner architecture, prompts, contracts and safety controls were unchanged.

The pilot then repeated on `c0a49f400666e0db336983e2bd631db01ab146d3` within the same campaign. The initial failed attempt remains a separate revision's result; it is not pooled with the following pilot or erased from the ledger.

## Seven-case pilot

First-pass validity requires construction, compilation and scenarios after exactly one physical model attempt. Independent business execution is measured separately. Calls include HTTP retries; repairs mean intent corrections.

| Case | FinalReview | First-pass | Calls | Repairs | Input / output tokens | Estimated EUR | Elapsed seconds | Independent variants |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| local | Yes | Yes | 1 | 0 | 2,333 / 148 | 0.01405323 | 3.400 | 2/2 |
| read_transform | Yes | Yes | 1 | 0 | 2,337 / 184 | 0.01501309 | 2.828 | 2/2 |
| conditional | Yes | Yes | 1 | 0 | 2,344 / 240 | 0.01650960 | 3.644 | 2/2 |
| collections | Yes | Yes | 1 | 0 | 2,357 / 921 | 0.03439354 | 11.479 | 2/2 |
| protected_cleanup | Yes | Yes | 1 | 0 | 2,342 / 274 | 0.01739092 | 3.338 | 4/5 |
| review_french | Yes | Yes | 1 | 0 | 3,067 / 1,644 | 0.05641798 | 16.710 | 8/8 |
| review_distractors | Yes | No | 2 | 0 | 3,668 / 1,782 | 0.06265271 | 319.069 | 8/8 |

Pilot totals: **7/7 FinalReview**, **7/7 within two calls**, **6/7 first-pass valid**, median calls **1**, eight physical attempts for seven logical interpretation requests, **zero intent repairs**, 18,448 input and 5,193 output tokens. Sum of case durations: 360.468 seconds. All 59 planner scenarios passed, but independent execution passed only **28/29 variants**, or **6/7 complete workflows**. Zero policy/capability safety violations were observed in the tested variants.

The pilot **failed** its independent execution gate. The 21 measured repetitions were therefore **not started**. None of the measured-cohort acceptance gates can be claimed satisfied; pilot rates are not a 21-run reliability estimate.

## Failure classifications

### Semantic misunderstanding: protected cleanup

The frozen request explicitly requires cleanup once after failure. The model placed cleanup in a finalizer but gave the cleanup invocation an `after` dependency on the write. The intent contract and shared model instructions state that this dependency requires completed main work; the compiler consequently guarded cleanup on the presence of the write result. An injected write failure produced only the write effect, with no cleanup.

Nominal execution, cancellation, rejected workflow approval and unavailable confirmation passed. The failure variant correctly failed the business oracle. This is not a builder deviation from the declared intent or a faulty oracle: the executable dependency contradicts the requested always-cleanup behavior. No prompt-specific patch, weakened expectation, rewritten artifact or additional model call was used to rescue the result.

This remains the business-reliability blocker. The existing scenarios validate the generated intent's execution but do not establish that its completion prerequisites express the user's lifecycle requirement. A focused future change should first specify and test cleanup ordering versus successful-completion prerequisites. This evidence does not justify another planner architecture refactor.

### Provider/transport failure: recovered HTTP 500

`review_distractors` received a complete HTTP 500 response, then HTTP 200 on its second physical attempt. The encrypted HTTP journal records distinct attempt identities and a backoff deadline; no Retry-After header was present. The shared provider HTTP layer handled recovery without an intent repair. The original model request/schema and both HTTP receipts were retained. The workflow then passed all eight independent variants.

This demonstrates live recovery from an allowed transient HTTP status. It does **not** demonstrate live uncertain-timeout recovery: no timeout, missing HTTP receipt, quota/authentication failure or exhausted retry allowance occurred in this pilot. Those paths remain covered by deterministic tests. No artificial live failure was injected.

### Review behavior

Both `review_french` and `review_distractors` passed nominal, failed-check, incomplete-check, publication-rejected, head-changed, cancelled, workflow-denied and permission-unavailable variants. The mocks independently checked a single clone/directory, the four requested checks and captured evidence, review instructions, APPROVE / REQUEST_CHANGES / COMMENT outcomes, cleanup and publication gates. These are live-generated workflows against fake integrations, not validation of actual GitHub or package-manager execution.

## Accounting, restart and isolation

Cumulative campaign estimate: **EUR 0.21643106**, based on returned usage and model-price/FX metadata; it is not an invoice. No unresolved HTTP allowances remain. The HTTP 500 had no generated-completion receipt; the ledger follows its existing explicit-rejection policy, counting the physical call and releasing its generation allowance. It does not invent output tokens for that rejected response. Missing uncertain-response usage would remain unknown with its conservative allowance.

Repeating the same pilot command on `c0a49f4` replayed saved results. The complete JSONL output was byte-identical: eight physical calls, token totals and cost stayed unchanged, with no additional dispatch or charge. Both commands exited 1 because the pilot correctness gate failed, not because replay failed.

The older `compact-intent-candidate-20260919-2d15b1` still has 14 reservations, 13 completion receipts and one uncertain request. Its known cost remains EUR 0.3672033158813263525305410123 plus unknown usage for that request. Its evidence hash before and after this campaign was identical: `61bb530740935a8a98a963504326f8a196f9116b5bc8cf71cf7b0333c4409db7`. No legacy uncertainty was resent or assigned zero usage.

## Validation and delivery

On `c0a49f4`, SDK 10.0.300, macOS arm64:

| Check | Result |
| --- | --- |
| Campaign tests | 14 passed |
| Full Release solution suite | 2,514 passed; zero failures; one opt-in live GitHub test skipped; 29 projects |
| Release solution and benchmark builds | Zero warnings/errors |
| AI.Core and Flow.Integrations packages | Passed, zero warnings |
| Eight offline corpus cases | All passed; fixture results are separate from live evidence |
| Native AOT planner publish and smoke | Passed, zero warnings |
| Trimmed self-contained single-file Agent publish | Passed, zero warnings |
| Published Agent persistence smoke | Schema-7 persistence, encrypted review drafts, tenant isolation and uncertain-publication replay passed |

The Agent publish used an isolated archive and a fresh persistence workspace. Bundled tool/frontend rebuilding was skipped for that persistence smoke; the normal solution build rebuilt its required frontends successfully. Frontend sources and production package sources were unchanged. No new warning suppressions were added.

The adapter correction was committed and pushed before the successful dispatches. This report preserves the failed preflight, failed cleanup expectation and recovered HTTP error rather than presenting only successful cases. The next paid measured cohort remains blocked until a clean seven-case pilot exists on one revision.
