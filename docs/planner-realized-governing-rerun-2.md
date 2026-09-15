# Frozen LOCAL validation: rerun 2

## Outcome

**PROVIDER BLOCKER — category A.** Campaign
`schema5-realized-governing-diagnostics-rerun-2` started LOCAL exactly once, with
zero consumed usage. It stopped during interpretation. MIXED and Stage 1 were
not run. Production remained frozen at
`2f8b177ec6076cfdedbf524bd72a01d3f1b457d7`.

## First meaningful blocker

Request eight failed without a verifiable receipt for decision
`interpret_623dee532557b0e28e211894`, page
`page_027c3d3c73783972f180d50171641395d2bbd9f2bea03809465b02cfa5d6bc0f`.
The retained exception is `LLMClientException`: **“The LLM provider could not be
reached.”** Phase: `intent`; gate: `response_contract`; location: `$`.

Its parent request had two decisions and returned verified `output_limit`, with
3,086 input tokens and 8,192 output tokens, all reported as reasoning. The planner
created two singleton partitions. The first partition request failed at its
unchanged 8,192 ceiling; the other child was never dispatched. This handled
partitioning was not itself a terminal blocker or semantic correction.

## Root cause

This is a provider/transport reliability failure. The retained exception does not
establish an underlying HTTP status or infrastructure cause. In particular, the
HTTP 500 from the earlier isolated diagnostic must not be attributed to this run.

Interpretation did not complete, so effect realizations, governing attachment and
canonical operation admission were not assessed. The run supplies no new verified
planner defect and does not establish admission convergence.

## Authority analysis

Production sources, all 22 production DLLs, model, all-low profiles, scenario,
declaration ports and attachments, catalog, host policy and budgets matched the
previous frozen manifest. The campaign retains sixteen durable reservations,
12,000 input / 9,600 dispatch target, 8,192 output and bounded 16,384 singleton
escalation. No ceiling, reasoning, proof or validation changed.

Only harness identity, comparison identity and LOCAL-only authorization changed.
The harness now rejects MIXED even after LOCAL success and rejects another start
when any checkpoint, report, budget or journal reservation exists. Mixed-case
offline assertions and receipt-reuse tests remain intact.

## Proposed action

**No production patch or retry.** The failed reservation, original requests,
receipts, checkpoint and budget remain encrypted. No replacement LOCAL or isolated
provider request followed the stop.

## Validation performed

**Offline:** 3,468 tests passed across 29 solution projects, including 975 planner
tests; one existing optional provider test was skipped. All 48 focused harness
tests passed. Six new cases cover fresh versus previously started durable state;
the gate regression now refuses MIXED even after a LOCAL pass.

Harness/test builds were warning-free and did not rebuild production references.
Both frontends and four package builds passed. Published Native AOT
planning/encrypted-restart and trimmed Agent.Server persistence smokes passed
again, as did six classifier/batch and 18 CodeReview reference cases without model
calls. Existing solution-build and publish evidence was retained for the unchanged
production binaries.

**Replay:** read-only audit checked all eight current requests and seven original
receipts, preserving the missing eighth receipt. Admission replay stops at
`INTENT_OPERATION_PROOF_MISSING`, because interpretation was incomplete. No
synthetic response was substituted. Previous LOCAL and isolated-request archives
remained unchanged.

**Live:** one fresh LOCAL, eight durable reservations, seven verified receipts and
one unverifiable dispatch. Six interpretation pages completed before the seventh
page was partitioned. The declaration fixture passed offline preflight; its
post-interpretation installation was never reached in this live run.

## Decision accounting

| Measurement | Result |
|---|---:|
| Verified calls / durable reservations | 7 / 8 |
| Unverifiable dispatches | 1 |
| Known input tokens | 15,295 |
| Known output tokens | 9,788 |
| Known reasoning tokens, included in output | 9,053 |
| Known final-answer tokens, derived | 735 |
| Failed request usage | Unknown |
| Largest estimated / verified actual input | 4,639 / 3,086 |
| Interpretation decisions exposed / on completed pages | 13 / 11 |
| Partition pages created / requests dispatched | 2 / 1 |
| Singleton escalations / semantic repairs | 0 / 0 |
| Realization / governing / occurrence-identity calls | 0 / 0 / 0 |
| Canonical operations, deterministic assignments and attachments | Not reached |

This harness-only rerun adds and removes zero planner decisions. The nine
engine-owned ConstraintsOnly runtime facets were already removed by the frozen
implementation; they are not new call savings. No live claims are made about the
expected classifier effect, governing evidence or preservation exclusions.

## Next gate

**LOCAL — completed with a provider blocker.** Its single-start allowance is
consumed. MIXED remains **not run**; no subsequent gate has opened.

The [redacted report](planner-realized-governing-rerun-2.json) records request and
receipt fingerprints, exact partition lineage, usage, offline checks and archive
integrity evidence.
