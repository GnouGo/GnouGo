# Frozen LOCAL validation: rerun 3

## Outcome

**PROVIDER BLOCKER — category A, external budget-conversion infrastructure.**
The single fresh LOCAL attempt under
`schema5-realized-governing-diagnostics-rerun-3` stopped **before the LLM transport
was called**. This run does not assess LLM availability or planner convergence.
MIXED and Stage 1 were not run.

Production remained frozen at
`2f8b177ec6076cfdedbf524bd72a01d3f1b457d7`. Only the harness campaign and comparison
identities changed.

## First meaningful blocker

`LLM_BUDGET_UNVERIFIABLE`, phase `intent`, gate `response_contract`, location `$`.
The first journal request was reserved for page
`page_fcd94956642457e7ec1cb9990a7f514be16a971199b668734325736d3c3de77a`.
It exposed these five interpretation decisions:

- `interpret_0f0bc1539179f1e1e4f291ff`
- `interpret_129eea96e794bd3a2e8d434f`
- `interpret_1ca9c160b29bfaae140d3375`
- `interpret_1f06d9513e9c8795104d3b2a`
- `interpret_259e5d02d09f870cf6815327`

No response, assignment or receipt was produced. The failure occurred before any
one of those semantic decisions could be assessed.

## Root cause

The retained stack identifies `ConvertToBudgetCurrencyAsync`, line 519, called
from `ReserveChainAsync`. That branch rejects an unavailable or invalid currency
exchange quote. The configured ECB integration supplied no usable conversion
quote for the monetary-budget check.

This occurs before `client.CallAsync`; the request journal is reserved earlier.
No budget reservation record was created. Therefore the journal's conservative
unverifiable-dispatch flag must not be interpreted as evidence of an LLM call.

The exact underlying HTTP/data cause was not retained. A null ECB result can have
several causes; this evidence does not establish a provider outage, a particular
HTTP status, stale rates or a model defect.

## Authority analysis

The engine correctly refused to dispatch without verifiable monetary accounting.
No budget was reset or bypassed. Model, all-low profiles, scenario, canonical
declaration ports and attachments, catalog, host policy, token ceilings and
sixteen-reservation bound matched the preceding frozen manifest.

All 22 production DLL hashes remained unchanged. The new campaign identity changes
owned reference hashes and their sorted packing order: the same 22 interpretation
decisions pack into 11 LOCAL pages versus 12 previously. Source/input fingerprints,
response semantics and limits are unchanged; no packing optimization was made.

## Proposed action

**No production change or automatic retry.** Preserve this stopped case and its
reserved request. Investigate the exchange-rate prerequisite separately before
authorizing further live validation. Do not remove the monetary budget or supply
an invented exchange rate.

## Validation performed

**Offline:** 3,468 tests passed across 29 solution projects, including 975 planner
tests; one existing optional provider test was skipped. All 48 focused harness
tests passed. No tests or production proofs changed for this identity-only rerun.
The existing tests cover single start, LOCAL-only gating, receipt reuse and refusal
to redispatch unverifiable reservations.

Harness and test builds passed without rebuilding production references. Both
frontends, four packages, harness fixture checks, six classifier/batch reference
cases and 18 CodeReview reference cases passed. These reference checks use no live
model evidence.

The earlier temporary publish directories had been removed. Native AOT
planning/encrypted-restart and trimmed Agent.Server persistence artifacts were
republished from retained Release outputs using `--no-build`; the trimmed publish
first required restoration of its missing runtime-specific assets. Both published
smokes passed warning-free. New artifact hashes are recorded separately, and the
diagnostic's frozen production DLLs were checked afterward.

**Historical replay:** the preceding LOCAL's eight requests and seven original
receipts were audited without provider calls. Before/after audit results match.
The current case's one request has no receipt; read-only admission replay stops at
`INTENT_OPERATION_PROOF_MISSING`. Missing evidence was not replaced or reinterpreted.

**Live attempt:** LOCAL started once from zero consumed usage. It stopped during
pre-dispatch budget verification. No model transport call, effect grounding,
canonical admission, behavior acceptance or construction occurred. Frozen binary
checks and the full archived-accounting fingerprint passed after the stop.

## Decision accounting

| Measurement | Result |
|---|---:|
| Fresh starts / coordinator reservations / journal requests | 1 / 1 / 1 |
| Actual LLM transport calls / verified receipts | 0 / 0 |
| Journal reservations without receipt | 1 |
| Input / output / reasoning / final-answer usage | Unknown; no receipt |
| Largest estimated / actual input | 3,291 / unknown |
| Interpretation decisions exposed / completed | 5 / 0 |
| Realization / governing / occurrence-identity calls | 0 / 0 / 0 |
| Partitions / singleton escalations / semantic repairs | 0 / 0 / 0 |
| Canonical operations / deterministic admission / attachments | Not reached |
| Planner decisions added / removed by this iteration | 0 / 0 |

The report's raw zero token sums aggregate an empty receipt set; they are not
measured usage. The nine engine-owned policy runtime facets are an existing frozen
implementation property, not new savings from this run.

## Next gate

**Isolate request** — the exchange-rate prerequisite, in a separately authorized
iteration. No further request was made in this task. Another LOCAL, MIXED and
Stage 1 remain unstarted.

The [redacted report](planner-realized-governing-rerun-3.json) preserves request,
failure and checkpoint fingerprints, offline evidence, accounting distinctions
and archive checks.
