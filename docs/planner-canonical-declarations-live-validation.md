# Canonical declaration Stage-1 validation

The single authorized fresh session **stopped before BehaviorReview**. It did not
reach the accepted-behavior checkpoint or a typed business outcome. No behavior,
canonical ports, executable skeleton or artifact was approved. Stage 2 and Stage 3
were not run. No production patch or replacement session followed this failure.

Production is frozen at `7e32988687d596c1e36065218599ab2316b9708a`; harness pin
`f954f0c` uses campaign `schema5-canonical-declarations-20260913`. The encrypted
campaign manifest records binary hashes and the unchanged scenario, catalog, policy,
model and budgets. The configured model is `OpenAi / gpt-5.5-2026-04-24`; every request
used `low`. Ceilings remain 12,000 input / 8,192 output, with a 9,600 estimated-input
dispatch target, concurrency four and five repairs per workflow/gate. No reasoning
A/B request was made.

## Live result

Session: `b8c6201e646247a1bcdf25aa5ab1d991`, final revision **23**.

| Measure | Result |
|---|---|
| Typed outcome | None; technical stop |
| Execution status | `stopped`, during capability preparation |
| Accepted behavior / artifact hashes | None |
| Verifiable model calls / reservations | 6 / 6 |
| Unverifiable dispatches / pending requests | 0 / 0 |
| Input / output tokens | 17,079 / 2,200 |
| Largest estimated / actual input | 7,432 / 5,805 |
| Decision pages | 6 completed; no correction or split pages |
| Distinct model decision IDs | 31 |
| Engine-resolved executable decisions | 0; other uninstrumented engine decisions unknown |
| Executable model decisions / hole exposures | 0 / 0 |
| User clarifications / answers | 0 / 0 |
| Repairs | 0 in every phase |
| Canonical declarations committed | 0; the invalid delta was not committed |

| Phase | Calls/pages | Input tokens | Output tokens |
|---|---:|---:|---:|
| Intent | 3 | 4,940 | 1,212 |
| Confirmation scope | 2 | 6,334 | 539 |
| Declaration adjudication | 1 | 5,805 | 449 |

The retained declaration response selected three distinct public names: required
`record`, optional `threshold` with default `100`, and required `classifiedResult`.
It retired one descriptive candidate and proposed three modifier assignments, with
no `same_as` aliases in this run. These are **candidate decisions**, not committed
ports or an accepted behavior. The earlier duplicate-span fixture remains a separate
offline regression.

## First blocker

Code: `DECLARATION_GROUNDING_UNRESOLVED`.
Canonical location: `/declarations/@ob_cf12755e9d631983`.
Diagnostic: `Declaration defaults conflict with each other or with required presence.`

Interpretation classified the fragment `standard otherwise.` as `default_value`.
Its containing clause defines runtime classification:

> Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.

Declaration adjudication attached that fragment to `classifiedResult` as a modifier,
but also selected the literal `false` from the condition as its `default` reference.
The coordinator rejected the default because the target is a required output.

This is a **default-scope and response-domain admission gap**. The request treats
preliminary `default_value` candidates as potential omission defaults and exposes
clause literals together with output targets. It does not sufficiently separate a
runtime fallback from an input omission default before model dispatch. The invalid
combination satisfied the response schema and failed deterministic admission.
It is not a token-limit, provider, confirmation-policy or missing-business-choice
failure. The proposed names and threshold default were correct, but the complete
assignment delta was not valid.

The completed page is
`page_23416a070f00613ecc5d516087147b5fea173963af738dad48397e66df9e53ef`.
Its exact request identity, reference-only response, accounting and binary/input
fingerprints are included in the [redacted report](planner-canonical-declarations-live-report.json).
Full request/receipt payloads remain in encrypted Schema-5 storage.

## Replay and validation

Strict offline replay from revision **18** consumes the retained declaration receipt
and reproduces the same code and location in one local advance. It dispatches **zero
provider requests** and verifies that the saved session, model journal and budget are
unchanged. No synthetic corrected answer was supplied. The campaign remains blocked
at revision 23 with no pending requests.

Before dispatch, **3,114 solution tests passed**, including **718 planner**, **841
Core** and **359 Agent.Server** tests; one optional provider test was skipped.
The final harness pin also passed all 359 Agent.Server tests. Solution/harness builds,
Core and Planning packages, Native AOT planning/encrypted-runtime persistence, and
trimmed Agent.Server EF persistence passed without warnings under the existing
publish exceptions. The offline harness checked six reference cases and 18 CodeReview
fixture contracts; these are not execution of an artifact from this session.

The captured previous behavior is preserved separately from synthetic adjudication
fixtures. Those fixtures demonstrate the corrected two-input/one-output assembly,
exact names, omission default, alias/modifier handling, stale-proof rejection and
review guards. This live run establishes the remaining default-scope blocker; it
does not demonstrate end-to-end convergence or call savings.

The single authorized start is consumed. No executable construction, final review,
independent execution of a generated artifact, artifact approval or later stage ran.
