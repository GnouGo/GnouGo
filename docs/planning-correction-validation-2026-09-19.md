# Compact intent correction validation

> Historical evidence: the Agent.Server review-publication subsystem described below was removed on 2026-09-24. Its draft, publication and replay checks describe the earlier implementation. Current workflows use configured MCP capabilities and generic approval mechanisms; see [migration details](github-mcp-workflow-execution.md).

This follow-up keeps the architecture, schema-7 sessions and planning budgets unchanged. The uncertain request in `compact-intent-candidate-20260919-2d15b1` remains stopped. Previous live results and accounting are preserved.

## Recorded-response replay

The existing benchmark runner now supports read-only replay of the original interpretation receipt under its reserved response schema. The current builder, compiler, scenarios, approval checks and independent execution oracle run with mocked integrations. No live model is constructed; no campaign record is written. Replay results are distinct from live reliability results.

The initial pilot responses at `8b63d5e` reproduce:

| Recorded case | Construction outcome | Independent execution | Classification |
| --- | --- | --- | --- |
| local | Rejected: undeclared computation binding | Not reached | Invalid intent |
| protected_cleanup | Rejected: dependency on cleanup group | Not reached | Invalid intent |
| review_distractors | Reaches FinalReview with the existing nested-input correction | 5/8 variants pass | Semantic misunderstanding of cleanup prerequisites |

The distractor response binds cleanup to successful publication. It passes nominal, failed-check, incomplete-check and denied/unavailable workflow-confirmation variants, but skips resource removal on publication rejection, a changed head and cancellation. The frozen prompt requires unconditional cleanup after acquiring the workspace. The oracle agrees with that requirement: publication remains prevented, but cleanup is missing. This is a business mismatch, not a capability/policy bypass. The saved response and engine semantics are unchanged.

## Instruction and repair-context correction

Sanitized regressions first demonstrated that repair prompts lacked business scope, finalizer placement and usable predecessor choices. Interpretation and repair now share short examples for bound calculation parameters and main work followed by resource cleanup. Dependency-error context includes operation kind, business scope, placement and structurally eligible predecessor IDs. The scope predicate is shared with structural validation; choices exclude self/cross-scope/cyclic dependencies and host identifiers. It also explains that depending on successful later work can skip finalization after failure. No candidate domains or mapping state are persisted.

All replacements remain model-supplied, atomic and subject to complete rebuild and validation. Tests verify bounded correction, schema/binding errors, repeated IDs in separate branches, confirmation insertion, guarded finalizers, cycles through captured branch/loop results, invalid replacement edges, cleanup on main-work failure/cancellation, and denial/unavailability of permission. The original bad dependencies are never silently removed.

A second sanitized form places primary work inside the cleanup block, as in the recorded protected-cleanup response. Fixing its structural dependency alone still fails the independent failure and cancellation expectations. The test deliberately preserves those negative outcomes: executable validity is not proof of requested business behavior, and changing the graph builder to reinterpret the intent would hide the mistake. The new examples explicitly place primary work in the main operations. Live-model improvement has not been measured.

## Provider reconciliation

Read-only inspection confirms 14 reservations, 13 completion receipts and one uncertain request in the existing campaign. Its original request schema remains stored, but the completion receipt, usage and structured provider failure metadata are absent. The historical runner did not retain a provider completion ID or provider logs usable for retrieval. The local reservation identity is not a provider response ID, so it cannot establish which remote response or charge belongs to this request. There is no authoritative recovery evidence available; the campaign remains stopped, with no resend or replacement campaign.

New failures retain only the existing safe typed metadata (kind, status, provider code, attempts, retry flags and dispatch/receipt-write stage) through encrypted KeyVault records. This is observability, not a reconciliation framework or retry policy. An unavailable failure-record store cannot bypass the durable reservation or replace the original failure.

The campaign evidence hash was identical before and after replay and inspection: `61bb530740935a8a98a963504326f8a196f9116b5bc8cf71cf7b0333c4409db7`. Known accounting remains 30,997 input tokens, 8,861 output tokens and EUR 0.3672033158813264, plus the unknown usage/charge of the uncertain request. This follow-up made zero live calls. No new pilot or measured cohort was dispatched.

## Delivery and validation

The changes were tested, committed and pushed separately on `feat/deterministic-planner-v2`:

- `4032dd5`: read-only original-response replay and isolation tests.
- `a894ba1`: generic computation/finalization examples, dependency context and deterministic regressions.
- `75506dc`: safe failure metadata and read-only campaign reconciliation inspection.

[Redacted replay results](planning-correction-replays-2026-09-19.jsonl) record the final source revision, original run identity, exact diagnostic codes, independent variant outcomes and evidence-based failure classification. Their single recorded call is a replayed receipt, not a new dispatch. Replay token/cost fields remain unknown; campaign accounting is reported separately above. No private prompt, response, generated artifact or credential is included.

Validation on `75506dc`, using SDK 10.0.300 on macOS arm64:

| Check | Result |
| --- | --- |
| Focused planner regressions | 94 passed |
| Benchmark campaign tests | 7 passed |
| Complete Release solution tests | 2,471 passed, zero failed, one opt-in live GitHub E2E test skipped; 29 test projects |
| Release solution build with warnings as errors | Passed, zero warnings |
| Flow.Core, Flow.Planning and Flow.Integrations packages | Passed, zero warnings |
| Eight offline fixture workflows | All reached FinalReview and passed all 31 independent execution variants |
| Native AOT planner publish and eight-case execution smoke | Passed; normal publish has zero warnings |
| Trimmed, self-contained, single-file Agent.Server publish | Passed, zero warnings |
| Published Agent persistence smoke | Schema-7 persistence, encrypted review drafts, tenant isolation and uncertain-publication replay passed |
| Recorded-response replay | Three original responses reproduced; zero live calls or campaign writes |
| Missing-receipt replay | Stopped with exit 2 and `REPLAY_UNAVAILABLE`; no dispatch |

The explicit Native AOT warning audit produced only the six previously documented Jint 4.16.0 warnings; no new suppression was introduced. The normal publish retains the existing publish-local exceptions described in the [smoke README](../tests/GnOuGo.Flow.Planning.Smoke/README.md). No frontend sources changed; frontend builds invoked by the component builds passed.

The first Agent publish attempts encountered overwritten runtime restore assets (`NETSDK1047`) in the shared working directory. Publishing a clean archive of the same commit in an isolated temporary directory passed without source changes. That published binary ran the persistence smoke against a separate temporary workspace. Bundled-tool publishing and frontend rebuilding were skipped for this persistence check; neither was changed by this work.

## Remaining limits

These deterministic regressions establish repair-context correctness and preserve execution/safety checks. They do not establish a new live-model success rate. The stopped campaign has no complete measured cohort and cannot satisfy the reliability gates. Corrected instructions still need live evaluation after authoritative reconciliation of the outstanding dispatch; missing usage must remain unknown. No request was retried and no replacement campaign was created.

Business cleanup mistakes can remain executable and pass nominal scenarios. The independent failure, rejection and cancellation variants continue to expose them. The engine does not silently change the model's business dependencies or move primary work out of finalization. There was no architecture change, schema migration, budget increase, real PR execution or GitHub publication.
