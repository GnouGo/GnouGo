# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. Amazon, Collections
and Review PR all stopped before approval or execution in the latest diagnostic
cohort. No business oracle passed. The gated 18-run comparison against `56942f5`
has not started; checkpoints, scoped reviews and CI are not business acceptance.

## Latest frozen diagnostics

Source `d488720a10ed97ec22f88e74f3d7ca532bd00141`, cohort `compactad488720a`, retained
`gpt-5.5-2026-04-24` and medium reasoning in every phase. Navigation target/ceiling
was 3k/12k with 16,384 output tokens; drafting 8k/16k with 8,192 output tokens;
Judges 4k/8k with 4,096 output tokens. Ordinary 64-call, correction, graph, cumulative
and financial allowances remained unchanged. The [aggregate evidence](compact-taskplan-d488720a.json)
retains source, settings, MCP binary, input and oracle hashes.

| Scenario | Calls / receipts / corrections | Input / output / reasoning tokens | Known cost | Recorded duration | Outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| Amazon | 2 / 2 / 0 | 1,360 / 17,173 / 16,896 | €0.463867 | 227.936 s planning | Empty output-limit receipt during interface extraction; no checkpoint |
| Collections | 21 / 21 / 4 | 33,970 / 16,466 / 13,049 | €0.589914 | 291.780 s run | Three checkpoints; selection/repair errors and a default-capacity review finding |
| Review PR | 30 / 30 / 4 | 89,174 / 27,715 / 21,714 | €1.135093 | 462.230 s planning | One checkpoint; interface defects and premature cleanup finding |

The 53 receipts cost €2.1888740780236381409401937261. No new uncertain call was
created. One completed response was empty: Amazon request 2 consumed its entire
16,384-token output allowance as reasoning, with an explicit output-limit receipt.
Its complete request was only 5,179 bytes / 1,983 estimated input tokens, and a
valid caller-input response was representable. This does not establish why the
model exhausted reasoning. The failure remains retained; no unchanged retry or
mid-session settings escalation occurred.

Collections incorrectly assigned `groupId` to a loop twice and encountered invalid
evidence and open-scope references. Its final Judge invoked a presentation preference
for a 100-item default to reject a supported 1,000-item capacity, although the original
source declared no numeric bound. This was not a missing-child finding. Explicit
business-size constraints and runtime capacity checks still apply; unspecified
capacity remains unverified.

Review PR's interface extraction omitted an explicitly declared input and restricted
another to the empty string without original authority. Bypassing that declaration
later did not repair it. Discovery correctly rejected an unsuitable adaptive operation
and selected the concrete clone capability after correcting its exact identity.
The final Judge treated absent cleanup in an intentionally incomplete fragment as
an omission despite no completion claim. The static original clone target remained
available independently of clone success. Later cleanup remains mandatory; this
observation does not establish a safe or complete executable plan.

All 608,489 bytes across 53 constructed requests reconcile with their components,
and every request met its captured input ceiling. Reasoning is included in provider
output tokens, not added to them. Completed-call latency was respectively 225.239,
277.787 and 434.671 seconds, excluding orchestration. No runtime latency exists.

| Phase | Requests | Complete bytes p50 / p95 / max | Estimated tokens p50 / p95 / max |
| --- | ---: | ---: | ---: |
| Intent | 7 | 3,129 / 5,321 / 5,321 | 1,299 / 2,030 / 2,030 |
| Selection | 11 | 7,565 / 11,516 / 11,516 | 2,778 / 4,095 / 4,095 |
| Discovery | 17 | 16,665 / 20,258 / 20,258 | 5,811 / 7,009 / 7,009 |
| Generation | 7 | 12,781 / 24,800 / 24,800 | 4,517 / 8,523 / 8,523 |
| Local Judge | 7 | 9,290 / 13,990 / 13,990 | 3,353 / 4,920 / 4,920 |
| Intermediate Judge | 1 | 9,005 / 9,005 / 9,005 | 3,258 / 3,258 / 3,258 |
| Correction | 3 | 17,558 / 21,784 / 21,784 | 6,109 / 7,518 / 7,518 |

These are median p50 and nearest-rank p95; conservative estimates are not billed
usage. The preceding [29ffd cohort](compact-taskplan-29ffd813.json) retained 94
receipts, €2.9606771527592641962143428417 cost and no execution. Its largest local
Judge was rejected before dispatch. These failed diagnostic cohorts do not establish
improved reliability or an architecture-only comparison.

## Focused corrections and experiment limits

Post-cohort source `dcca6ce` clarifies call-only `groupId`, removes the presentation
default as an invented business limit, and uses one nullable-default input shape
while preserving explicit literal null. Further fresh-only extraction guidance,
nullable string-enum presentation and scoped cleanup review corrections passed
623 Compact regressions with warnings as errors. The schema changes remain controlled
presentation experiments;
neither the cause of reasoning exhaustion nor improved convergence is established.
No new runtime, permission bypass, automatic escalation or final whole-workflow
Judge is introduced. A fresh frozen cohort must validate these changes live.

## Campaign accounting and retained uncertainty

The post-diagnostic encrypted audit verifies 1,099 request identities / 1,089
receipts. All ten receiptless identities retain permanent closures; the journal
separately counts nine attempts with unknown usage. No reservation was released.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 31.879814271749755620723362654 |
| Unknown-usage reservations | 3.6010530525193281791522260728 |
| Spending/reservation upper bound | 35.480867324269083799875588727 |
| Available allowance | 204.549236508437220742569358573 |

Audit evidence: `d112c5c4336537d1c06904d854492c12873e0900dd91f83fae71a3bacd512f6e`.
The prior Collections interruption closure remains recorded in the immutable
[29ffd archive](compact-taskplan-29ffd813.json), with its full reservation and
permanent replay/continuation prohibition. The [3adc archive](compact-taskplan-3adc335.json)
remains its earlier pre-authorization snapshot. This audit counts existing closures;
it does not independently reauthorize or rewrite them.

Separate lost historical exposure of €159.96989616729369545755505266 stays within
the original €400 authorization. No ceiling increased. Future paid dispatch must
recheck balance and provider readiness; uncertainty must never be replayed.

## Validation and remaining acceptance

The interrupted-process operator passed 495 retention/campaign tests with warnings
as errors. The combined fresh schema and review changes passed 623 Compact tests
with warnings as errors (no failures or skips). CI for exact heads
[`29ffd813`](https://github.com/GnouGo/GnouGo/actions/runs/39811045971) and
[`d488720a`](https://github.com/GnouGo/GnouGo/actions/runs/39818158151) was still running
at this snapshot; no final result is claimed. The prior
[`3adc335` CI](https://github.com/GnouGo/GnouGo/actions/runs/39532215780) passed 6,331
selected-suite tests, one separate published Git test, five Flow packages, benchmark
build and Linux Native AOT, with one browser opt-in skip and no compiler warnings.
Its local full-solution and published-storage logs did not survive; selected-project
CI does not establish those lost local outcomes.

All three independent business oracles and final broad validation remain required.
Collections must execute both ordered, duplicate-preserving variants with reusable
parallel structure; Amazon needs complete observations, exact workbook contents
and browser closure; Review PR needs pinned provenance, actual checks, local reports
and cleanup. Artifact-bound review and exact individual confirmations remain mandatory.
Only after all diagnostics pass may the 18 alternating measured runs begin, with
frozen settings and disclosed arm differences.

## Immutable evidence

The [d488 aggregate](compact-taskplan-d488720a.json) indexes retained detailed metrics
and diagnosis hashes. Prior [29ffd](compact-taskplan-29ffd813.json),
[3adc](compact-taskplan-3adc335.json), [4a43](compact-taskplan-4a43ea1.json),
[8952](compact-taskplan-8952a0a.json), [743](compact-taskplan-743e4a3.json),
[410](compact-taskplan-41077d0.json), [663](compact-taskplan-663cf72.json),
[1984](compact-taskplan-1984bdc.json), [afc](compact-taskplan-afc342c.json),
[5c](compact-taskplan-5c01f98.json), [08ad](compact-taskplan-08ad5db.json) and
[cd174ea](compact-taskplan-cd174ea.json) archives retain their original failures,
accounting and validation limitations. [October 19](compact-taskplan-live-2026-10-19.md),
[October 20](compact-taskplan-live-2026-10-20.md) and
[historical components](compact-taskplan-request-components-2026-10-20.json) remain
unchanged. Original payloads stay encrypted; reports contain safe metadata and grant
no execution authority.
