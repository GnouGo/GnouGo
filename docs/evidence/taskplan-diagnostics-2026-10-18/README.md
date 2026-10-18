# TaskPlan diagnostic and correction follow-up — PR #119

This delivery addresses the generic planner and review defects found in the
[retained Amazon/code cohort](../business-judge-live-2026-10-18/README.md). The live
failures remain unchanged. This follow-up uses offline fixtures, replay, local
workbooks and published smoke checks; it does not dispatch paid models or execute
Amazon or the external code-review workflow.

## Behavior and compatibility

- Fresh sessions collect accepted-output and commitment findings together before
  issuing a correction. Progress version 8 captures the combined authorized scope;
  patches remain atomic and undergo complete validation. A disconnected result is
  not permission to change accepted obligations or choose an unrelated producer.
- Private repair envelope 11 constrains known output and explicit operation-input
  value types. String outputs do not offer numeric arithmetic. References, nested
  assembly, exact names, ownership and workspace restrictions remain intact.
- Input review version 2 distinguishes explicit independent extraction, conditional
  runtime detection and whole-input extraction. `infer_each` alone does not prove
  itemwise processing. Readiness maps back to authored inputs, reports known source
  bytes separately from tokens, and remains unverified until actual admission.
- Judge review version 2 adds these deterministic readiness facts and generic
  assertion/routing/failure semantics. Bounded invalid-reference diagnostics are
  retained separately from validated responses. Mixed valid/invalid reports stay
  wholly inconclusive, confer no edit authority and do not create another call.
- Historical profiles, pending requests, receipts and approval identities retain
  their saved behavior. Tests pin historical repair-schema/authority hashes and
  compare valid v15 graphs/YAML. TaskPlan, format 10 and public APIs are unchanged.
- Creation captures private profiles before persistence. An old queued session at
  revision zero retains absent or saved profiles after deployment; deserialization
  does not turn it into a fresh creation. Flow's authored resume definition remains
  unchanged.

The planning skill already contains the governing generic-validation, advisory-Judge,
scope and budget principles; it was read before this follow-up. No scenario recipe
was added to shared instructions or production planner logic. Production documentation
describes the new private profiles and historical fallbacks.

## Independent fixture assertions

The combined-diagnosis regression observes output-nullability and disconnected-lineage
findings before a repair. Its positive candidate fixes the two exact bindings in one
repair (two total calls); an unrelated status edit is rejected. Historical sequential
diagnosis remains separately covered.

The workbook fixture independently checks missing-link error rows, ordered synthetic
`read_item` calls, repeated URLs, long identities, exact Unicode/text cells, explicit missing data, empty
collections and cleanup. It uses real local XLSX writing/reading and rejects every
inference call. Its assertion-failure and writer-failure variants produce no successful
public result. Existing large-observation and cross-page fixtures retain admission,
complete-collection and cleanup checks.

Judge tests cover false or missed observations through existing independent oracles,
invalid references, mixed authority, null-valued locations, escaping, bounds, cache
reuse and recovery. Successful response validation does not upgrade a model conclusion
to deterministic business proof. Scripted reports cannot establish real-model
precision, recall, convergence improvement or the 25% live error-reduction gate.

## Frozen fixture comparison

Candidate `25dc994d45925054859cdb0874428dceb432b62f` was committed before collecting
48 scripted runs: eight business cases, three repetitions, with and without the
Judge. Both arms use twelve calls, four corrections, 32,000 input tokens and 32,768
output tokens. Creation defaults were not changed in this follow-up.

The later test-only commit `2b20c8b` removes a stale v10 seed from a fresh-session
fixture that expects v15, and strengthens the captured historical-profile assertion.
Production code and all 436 frozen Release benchmark files remain unchanged. All
19 affected browser/workbook variants and four queued-session checks pass; the
[rerun results](test-adjustment-summary.json) and [source hashes](test-adjustments.json)
are separate from the frozen comparison.
Test-only commit `bbd1061` also updates the fresh host correction-version expectation
from 6 to 8. All eight host variants pass with their durability, tenant, receipt and
recovery assertions intact ([source](commitment-host-adjustment.json),
[rerun](commitment-host-adjustment-result.json)).

| Measurement | Judge disabled | Scripted Judge enabled |
| --- | ---: | ---: |
| First proposals passing independent fixture oracles | 24/24 | 24/24 |
| FinalReview and execution-oracle success | 24/24 | 24/24 |
| Median logical calls | 1 | 2 |
| Corrections | 0 | 0 |
| Safety violations / introduced regressions | 0 / 0 | 0 / 0 |
| Median actual tokens and cost | unknown | unknown |

The scripted Judge returns no findings. Its 24 true-negative fixture labels do not
measure real-model sensitivity or false-positive rates; this all-correct baseline
cannot establish a relative failure reduction. The focused regressions separately
exercise correct and false findings, unusable reports, combined diagnosis and bounded
correction. No live model or Amazon execution was dispatched.

[Manifest](manifest.json), [commands](commands.json), [summary](summary.json),
[control rows](control.jsonl), [Judge rows](judge.jsonl) and
[post-run hash verification](candidate-verification.json) retain the settings,
per-stage accounting and unchanged source/binary evidence.

## Validation and limits

[Consolidated validation](validation.json), [validation attempts](validation-attempts.json) and
[Release/AOT/recovery results](release-validation.json) retain commands and log hashes.
Unknown model usage and cost in scripted fixtures remain unknown, not zero. No campaign
budget, retry allowance or historical run was reset.

The complete serial solution invocation used `-m:1 -warnaserror`: 33 assemblies,
6,234 cases, 6,202 passed, 19 failed and 13 skipped; its exit code remains **1**.
All 19 failures were the stale fixture expectations described below. The corrected
19-case business-composition family, eight-case host-correction family and four
compatibility cases passed in separate invocations (31 passing executions).
Those reruns overlap the suite; they are not added to its total or presented as
a clean second full invocation. No observed failure remains unresolved.

All 1,741 Planning tests passed in the complete run. The 13 skips are seven
Windows-only cases, one opt-in browser smoke and five opt-in live-model cases.
The frontend production build, Razor interaction checks, eight Release/AOT/published
recovery/build steps and 48 frozen fixture runs passed. No paid provider was dispatched.

The initial component run found test fixtures that mixed newly captured policies with
historical version assertions and an incomplete YAML test catalog. The corrected tests
retain the historical cases explicitly. Earlier red and failed checks are recorded
separately from final checks; their counts are not combined into a passing run.
Final review also found the queued revision-zero compatibility edge. A failing
regression preceded its fix. The earlier serial run was stopped because its binaries
predated that fix, and is retained as incomplete rather than a passing suite.

The subsequent serial run exposed eleven stale fresh-session fixture assertions:
they explicitly supplied compiler v10 while expecting creation to replace it with
v15. The fixture now omits that historical seed; all execution, workbook, cleanup
and v15 assertions remain. All 19 variants pass in the separate corrected rerun,
and four compatibility cases pass with an additional assertion that explicitly
captured v10 remains v10. This test-only correction does not change production
behavior. The failed full invocation is retained separately from those reruns.
Eight further host failures expected old correction version 6 for fresh sessions.
Their expectation now matches captured version 8; the entire eight-case family
passes in its separate rerun. Historical version assertions remain unchanged.

One provisional new workbook fixture used the default document workspace before its
temporary-root setting was made explicit. It wrote `products.xlsx` in that default
workspace, then its empty variant replaced the fixture rows with headers. The prior
existence of a user file cannot be established. The current file was left untouched;
no original contents or backup are known. Subsequent runs assert the dedicated temporary
root before writing. This is a test-side-effect limitation, not evidence that the
provisional run was confined correctly.

Two provisional host logs were overwritten by reruns. Their failure counts and causes
are retained from the recorded tool output, with raw-log availability marked false.
The final host log is not presented as preserving those earlier logs.
