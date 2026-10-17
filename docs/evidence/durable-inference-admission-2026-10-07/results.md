# Durable admission validation

Candidate: `fcc75db8e7fe37dbaed58d3cbfe807c52f4995e7`. No paid calls, live runs, budget changes or historical reconciliation occurred.

The [correction](../../durable-inference-admission.md) makes accounting readiness precede external work, stores an admitted quote with each new request, and settles against that quote. A verified pre-dispatch refusal becomes a durable error, not an unknown external completion. It does not make the external quote service available or establish live workflow success.

## Deterministic results

- **71 focused tests passed**, including **19 new admission cases**, existing campaign accounting and configuration precedence.
- The full warning-as-error solution run completed with **4,727 passed, one failed, 12 existing skips**, across 33 suites. Affected suites passed: Flow 1,120; integrations 97; host 701; planner 1,059; encrypted persistence 17.
- The sole full-suite failure was the unchanged Browser link-follow case, which returned unsuccessful after 30 seconds. The original assertion did not expose the runtime cause. Both cases passed unchanged in isolation, then the entire Browser suite passed **88/88**. The first failure remains recorded; these reruns do not make the original full-suite run green.
- Flow.Core Release packaging, planning Native AOT on osx-arm64, self-contained trimmed Agent.Server publishing and its encrypted recovery smoke passed. Optional model-metadata regeneration and frontend rebuilding were skipped; neither changed.
- [Validation metadata](validation.json) includes command, per-suite results, log hashes and point-in-time CI status. Pending CI checks remain unverified.

The regressions independently check encrypted campaign/tenant isolation, zero provider calls before readiness, quote validity and expiry, reuse across restart, settlement after quote-service loss, request-hash-bound refusal replay, normal failure cleanup and blocked cleanup after uncertain dispatch or failed receipt publication. No threshold, assertion, execution oracle or budget was relaxed.

An initial regression also demonstrated that mapping classified a budget error after dispatch as completed. The fix now requires explicit `not_started` evidence, matching the existing ordinary LLM executor. Mapping generation, scripts, source grounding, caching and the two-attempt allowance are unchanged.

## Preserved live evidence

The previous [Amazon failure](../canonical-items-and-actions-2026-10-07/amazon-result.md), its unresolved invocation and all unknown reservations remain untouched. The last retained campaign upper bound is EUR 96.942719 / 150; this correction did not access or mutate paid campaign state. A future evaluation needs fresh readiness, explicit authorization and new execution identities. PR #117 remains draft.
