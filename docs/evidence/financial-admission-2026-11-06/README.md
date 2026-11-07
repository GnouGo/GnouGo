# Financial admission correction and audit — 2026-11-06

Implemented in `c09bb520` and `9faa818c`, with shared finite-scope hardening in
`dd930f98` and restored Server coverage in `77dfe367`. The experimental policy permits warnings
for future-call forecasts while enforcing the current reservation and settlement.
The campaign ceiling, historical charges, reservations, model and protocol remain
unchanged. **Paid provider qualification and Amazon generation remain unachieved**:
the fresh actual-host trial stops before dispatch on incomplete external accounting.

## What the €1,434.64 means

The original probe's refused exposure was exactly
**€1,434.6359532230043326619455983**, not a provider invoice:

| Component | EUR, rounded here only |
| --- | ---: |
| Campaign measured usage × captured tariffs | 475.28 |
| Unresolved campaign attempt reservations | 222.72 |
| External provisional holds | 733.89 |
| Proposed probe reservation | 2.74 |
| Total | 1,434.64 |

[Exact decimal decomposition](exposure-decomposition.json) preserves every digit.
The probe has no future-review forecast, so changing future forecasts to warnings
cannot make its current reservation affordable. The campaign's own exposure is
€698.00453661827155318192155264, leaving €42.02556721443475136052339466 **before
external sessions**. That is not verified globally available funding.

The €475.28 category comes from measured tokens and captured tariffs/FX; it is the
best priced consumption evidence retained here. It is not an invoice or independently
verified provider-account debit. Provider-reported monetary cost is unavailable.
Reasoning tokens remain included in output totals.

## Where the €740.03 ceiling comes from

This is the existing benchmark campaign's persisted spending authorization, not
Agent.Server's ordinary €50 session setting or a provider-account balance.

On October 19, the recorded authorization restricted a fresh campaign's allowance
to €400 minus the independently retained prior lost-ledger upper bound
€159.96989616729369545755505266: **€240.0301038327063045424449473**.
The stored decimal allowance is conservatively below the exact subtraction by
€0.00000000000000000000000004.
The missing ledger was explicitly acknowledged; its receipts were not reconstructed.
Subsequent recorded authorizations added €200 on October 25, then €100 each on
October 27, 29 and 30. This yields **€740.0301038327063045424449473**. The initial
code default of €50 was replaced, not added. The lost-history allowance is already
reflected in the reduced ceiling; it is not charged a second time in this campaign.
See [the authorization chain and hashes](ceiling-origin.json).

## Reservations, potential orphans and duplicate checks

The fresh [campaign audit](campaign-audit.json) groups the existing accounting by
366 session owners. It finds 162 unresolved physical attempts holding €222.72:

| Observed HTTP status | Retained attempts |
| --- | ---: |
| 500 | 115 |
| Unobserved | 38 |
| 502 | 6 |
| 504 | 1 |
| 400 | 2 |

The [attempt audit](retained-attempt-audit.json) retains exact request identities,
record hashes, amounts, input/output bounds and safe status categories; its
[read-only extraction](retained-attempt-query.cs.txt) uses the public record API.
A 500,
timeout or missing status does not prove zero provider consumption. In 76 request
records a successful receipt coexists with unresolved earlier attempts: the
receipt settles its own successful attempt, not its earlier retries. These are
separate physical liabilities, not duplicate accounting for one successful response.
Even the two 400 responses lack billing evidence sufficient to turn them into
zero-cost receipts. No historical reservation was released or silently repriced.

There is no legacy aggregate budget in this campaign, hence no overlapping legacy
attempt charges. Its ten zero-attempt HTTP records hold **zero campaign money**.
The failed Server session `11386753f6ce4c4eb456deb20376e057` additionally retains
€1.9655709807433945364979847738 in its application budget despite its bound
pre-dispatch refusal. This is a reconciliation candidate, retained explicitly;
it is not claimed as actual provider spend. The campaign has an empty zero-money
record for that session, so including the application hold externally does not
charge that amount twice. Sessions `9fce…` and `1a54…` are outside campaign ownership.
Parent/child budget projections are not summed as independent calls.

Independently regrouped decimal totals differ from the durable aggregate by only
conservative decimal rounding: the durable aggregate is slightly higher. No
material double counting was found in the inspected scopes. This does not claim
that missing historical billing evidence has been recovered. The
[rounding check](rounding-checks.json) uses decimal precision 75, not binary floats.

## Why the external €733.89 is not known consumption

[External hold components](external-hold-components.json) retain original nominal
estimates for completed unpriced usage, pending requests, `local-01` and the
additional refused Server reservation. They are provisional safeguards, not newly
priced historical charges or a verified liability upper bound.

The [9fce…](external-session-9fce.json) and [1a54…](external-session-1a54.json)
evidence contains **325 completed receipts without a captured cache-write tariff**
and two unresolved requests. Measured cache-write tokens total 2,048,947. Their
captured input/output rates exist, but cache-write pricing does not. The gateway's
Anthropic adapter maps the explicit `cache_creation_input_tokens` field; these are
not inferred cache misses that can safely be relabelled. The inspected receipts do
not supply an actual monetary charge. Today's public catalog cannot price a past
private deployment. Verified global available funding is therefore **unavailable**,
not €42.03 and not zero-cost external consumption.

A further targeted metadata read on November 7 UTC inspected 327 receipts and
328 parsed HTTP response bodies. Neither [9fce…](financial-field-audit-9fce.json)
nor [1a54…](financial-field-audit-1a54.json) supplied monetary cost, fee, currency,
credit or billing fields outside normalized usage. The
[query](financial-field-query.cs.txt) excludes generated business content and
credential headers; it performs no inference or settlement. This is additional
missing-evidence proof, not a zero-cost invoice or permission to release a hold.

Fresh read-only runs of the new projection against the original encrypted budgets
confirm their earlier hashes are unchanged:

| Session | Priced usage estimate EUR | Unresolved holds EUR | Original estimates for unpriced completions EUR |
| --- | ---: | ---: | ---: |
| [9fce…](session-financial-projection-9fce.json) | 0.08609 | 2.02227 | 547.04818 |
| [1a54…](session-financial-projection-1a54.json) | 0.09732 | 3.03207 | 177.68353 |
| [1138…](session-financial-projection-1138.json) | 0 | 1.96557 | 0 |

The unpriced original estimates are reported separately; they are not already
included as priced usage in those snapshots. The external hold accounts for them
explicitly. All three projections leave provider cost and available funding null.
Budget attempt counts describe reservation entries, not observed HTTP sends. In
particular, `1138…` has one unsettled budget entry but zero provider HTTP attempts
in its retained transport evidence; the projection alone cannot establish dispatch.

## Correction and activation

New Limited sessions can capture
`ApplicationLlmBudget:FinancialForecastMode=experimental_warning`; the default is
`strict`. The protected `/settings/llm-budget` preference overrides host defaults
for new sessions only, using the existing operator cookie and antiforgery protection.
Existing captured sessions keep their exact policy. Explicit workflow limits do
not inherit the experimental authority.

Only unissued future-call/review forecasts can warn. Current physical reservations,
actual settlement, unresolved exposure, missing prices/FX, permissions, mandatory
reviews, finite request timeouts, bounded retries and uncertain-replay barriers
remain enforced. A real overrun is recorded before rejection. Reviews may stop
later if they cannot be funded; the warning grants no approval authority.

Additional reproduction found a shared-runtime gap: restored unpriced history was
guarded during experimental planning admission but not strict admission or direct
execution. Four Strict/Experimental × unpriced/unattributed-uncertain regressions
failed before correction. The history check now runs in the common finite-scope
pre-call gate. It preserves known bounded journal reservations and exact receipt
recovery. A released concurrency gate no longer permits a new call to use an
unverified earlier failure as zero-cost history. No old amount or policy is rewritten.

Historical estimates can be excessive. Two inspected attempts reserve €32.574648…
each against 1,017,232 input tokens, with retained serialized request bodies of
169,950 and 28,029 UTF-8 bytes. New experimental current-input reservation bounds
use the serialized envelope and configured phase target instead of the full
physical input capacity or future-review context multiplier. Physical admission
and captured output allocations remain separate and unchanged. Old records retain
their original bounds; this change does not justify refunding uncertain history.

**Inspect session finances** now reads the existing protected encrypted session
budget through `GET /api/planning/{id}/financial`. It separates priced usage,
unresolved reservations, original estimates for unpriced completed usage, local
exposure, captured allowance and forecast warnings. Unknown invoices/global
funding remain explicit. The read does not scan tenant history, mutate accounting
or introduce another store, worker, planning cache or call list. The benchmark's
explicit `financial-audit` command supplies the campaign-wide, per-session view.

## Actual-host and paid qualification

The [final configuration comparison](configuration-final.json) passes all five
selection checks for `75abc193…`, `OpenAi / gpt-6.1-sol-2026-09-29`. No new amendment,
provider, model or protocol was substituted.

The initial run `finance-experimental-9faa-20261106-2256`, source `9faa818c`, used the actual
Agent.Server host, protected saved experimental setting, existing Limited campaign
and unchanged €733.89 external hold. [Frontend readiness](actual-host-readiness.json)
passed. Its [bound refusal and first stack](experimental-live-refusal.json) show
`LLM_BUDGET_UNVERIFIABLE`, reason `external_financial_history_incomplete`, before
campaign reservation or provider transport: **zero HTTP attempts, no paid response,
no completion receipt**. The new guard also avoids creating another campaign
incident merely to discover that external accounting is unverified.

Final run `finance-final-77df-20261106-2349`, source `77dfe367`, independently repeats
the actual host check with fresh request identity and final assembly hashes. Its
[readiness](final-host-readiness.json), [refusal/stack](final-live-refusal.json) and
[fresh ledger audit](campaign-audit-final.json) confirm the same first financial
cause, zero provider HTTP attempts and unchanged ceiling/amounts. No paid response
or generation success is inferred from readiness or a reserved-call counter.

No subsequent Amazon session was dispatched around that refusal. Obtaining the
missing original billing/tariff evidence and resolving the outstanding receipts
through existing mechanisms is the first remaining funding obstacle. No Unlimited
switch, new authorization, ceiling increase, reservation release or uncertain replay
was used to obtain admission.

## Validation

Final accepted focused runs: **1,529 Flow tests** and **176 Server tests**, zero failures
or skips. They overlap later solution tests and must not be added to them. Tests
cover future warnings, current reservation refusal, charged actual overrun,
changing estimators, unpriced history, explicit child limits, protected persistence,
unchanged historical captures, missing history in planning/execution, restored
finite budgets under both threshold modes and the €50/high-tariff case through Server adapters.
Strict blocks before inference; experimental reaches mandatory final review with
the same 128,000-token allocations under a controlled model.

The actual-host controlled HTTP browser path has five logical calls and six
physical attempts, including a retained failed retry. It checks finance reads,
authorization, antiforgery, wrong ownership, reload/restart and unchanged captures.
Its [screenshot](controlled-browser/session-finances.png) and
[financial response](controlled-browser/session-finances.json) are labelled
**controlled-provider evidence**, not paid-provider success. The existing shared
LLM inspector and functional timeline remain in use.

Warning-free Release solution build and frontend production build passed. Core,
Integrations and Planning packages, macOS ARM64 Native AOT CLI/storage smoke, and
published trimmed managed Server encrypted-storage/exact-receipt smoke passed.
The CLI's existing two pinned EFCORETASKS experimental notices were checked against
the repository's exact-notice audit; no other warning/error was accepted. Full
PowerShell/Windows publication checks are unavailable here. See
[source-bound validation](validation.json) and [final published checks](published-validation-final.json).
Reproduced failures and intermediate test-assertion corrections are retained there,
separate from accepted results. The original `9faa818c` and final `77dfe367` full
solution test runs are recorded independently when complete; overlapping totals
are never added together.

The original feature freeze completed with **7,970 passed, zero failed, 13 skipped**
in 33 assemblies. Its default skips are seven Windows-only cases, five opt-in
Copilot live cases and one legacy clarification browser test. The last also passed
separately on final source when supplied the existing staged Playwright module:
one passed, zero failed/skipped, including cancellation and exact receipt recovery.
This supplementary run does not change the original full-run counters. The final
hardening freeze's full result is recorded separately.

User-staged solution and `.vscode` changes remain preserved and excluded from these
commits. No merge or release was performed.
