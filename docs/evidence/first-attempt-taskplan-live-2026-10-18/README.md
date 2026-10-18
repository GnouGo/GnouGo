# First-attempt TaskPlan live comparison — 18 October 2026

**The comparison is inconclusive. The first baseline session stopped on uncertain
provider completion before returning a proposal. No candidate session or workflow
execution was attempted.** This is retained live transport failure evidence, not a
planning-correctness result.

The user authorized live testing, then selected “Baseline/candidate comparison:
11 cases, three repetitions each.” The intended collection contains 66 sessions:
the eight existing business cases and three local-file cases, three repetitions
per production revision. The collection alternates baseline and candidate by case.
No additional pilot or diagnostic inference was requested.

## Frozen preparation

- Baseline production: `676b5acef3442b1436149eaf9581c7d5e99fe1a3`.
- Baseline with the identical measurement harness:
  `16d02a11936d7f8fe020b3b4e9200c25546b49bb`.
- Candidate: `8168c140ed58361aae7ec1446e9daed580f16251`, containing the
  [previously validated implementation](../first-attempt-taskplan-2026-10-17/README.md).
- Both are clean, isolated, detached local commits. The user's branch was not
  committed, switched or pushed. The baseline production tree is unchanged.
- Both Release harness builds passed with `-warnaserror`, zero warnings and zero
  errors. Candidate production and harness hashes match the earlier frozen
  implementation. The same 53-file Cmd Release directory is frozen for both sides.
- The existing `schema-portability-20261002` encrypted campaign remains in use,
  including all historical receipts, reservations and the existing EUR 200 ceiling.
  No spending allowance was reset or increased.
- Provider/model remain `OpenAi` / `gpt-5.5-2026-04-24`, medium reasoning, 96,000
  input tokens, 32,768 output tokens, eight planning calls and two repairs per session.
  Configuration, pricing and currency readiness passed before dispatch.
- Generated artifacts require the existing explicit revision/hash-bound approval
  and requirement acknowledgments before execution. No approval was generated.

See [freeze](freeze.json), [comparison manifest](comparison-manifest.json),
[provider readiness](provider-readiness.json), [binary hashes](cmd-binary-hashes.json.gz)
and [validation](validation.json). The manifest's historical `ceiling_eur: 50` is
the original policy value; the effective authorized ceiling is EUR 200 in the
retained spending ledger and freeze record. Both cohorts use that same ledger.

## Attempt and diagnosis

| Measurement | Observed result |
| --- | --- |
| Session | Baseline `local`, repetition 1 |
| Request | Return numeric `result = 6 × 7`; no inputs or external calls |
| Final state | `stopped` |
| Logical planning calls / transport attempts | 1 / 2 |
| Repairs | 0 |
| Elapsed planning time | 614,032 ms, about 10 minutes 14 seconds |
| Submitted proposals | 0 |
| FinalReview / approved artifacts / workflow executions | 0 / 0 / 0 |
| Diagnostic | `MODEL_DISPATCH_UNVERIFIABLE` |
| Termination reason | `uncertain_dispatch` |
| Completed baseline / candidate cohorts | Neither; 1 baseline session attempted, 0 candidate sessions attempted |

The saved diagnostic classifies the failure as retryable transport loss with no
retained completion. Both physical attempts remain unconfirmed. The collector
stopped on the harness's exit code 2 and did not dispatch any of the other 65
sessions. The original request identity and its cumulative reservations remain;
there was no manual resend, replacement identity or closure of the uncertain run.

The [sanitized result](baseline-local-1.json) and [diagnosis](diagnosis.json) retain
the exact identity, state and measurements. Request/response material stays in the
existing encrypted record store. [Collection status](collection-status.json), the
[driver](collect.py) and compressed logs preserve the collection procedure and stop.

No first proposal exists to assess for structural or independent business
correctness. Omitted requirements, extra operations, redundant tasks and execution
safety outcomes therefore remain unknown. No workflow effects occurred.

## Accounting and comparison

| Campaign accounting | Before | After |
| --- | ---: | ---: |
| Known estimated cost, EUR | 137.041241 | 137.041241 |
| Unconfirmed cost reservations, EUR | 17.260933 | 19.877944 |
| Combined upper bound, EUR | 154.302174 | 156.919185 |
| Authorized ceiling, EUR | 200 | 200 |

The two attempts add **EUR 2.617011 in reservations**, covering 192,000 input and
65,536 output tokens. Actual new usage and cost are unknown, not zero. The remaining
allowance under the retained upper bound is about EUR 43.08. These figures are
usage estimates/reservations, not invoices. See [before](accounting-before.json)
and [after](accounting-after.json).

The read-only [first-proposal comparison](comparison.json) returns `inconclusive`:
the cohorts are incomplete and usage is unknown. Its zero classified failures and
empty-cohort safety flags are not evidence of zero business failures or safety
equivalence. No relative failure reduction, token/call improvement or live business
correctness claim can be made from this attempt. The 25% improvement gate remains
unestablished.

Further collection depends on resolving the retained transport uncertainty through
the existing recovery policy and obtaining a working provider response. It must
preserve this failed slot, frozen identities and the shared cumulative ledger.

Follow-up: the separately requested [Amazon live evaluation](../first-attempt-amazon-live-2026-10-18/README.md)
retains this exact request as inconclusive after a regression-tested fix recognizing
its already exhausted captured uncertainty allowance. The original attempts,
reservations and failed comparison slot remain unchanged. The comparison described
above has not resumed and remains inconclusive.
