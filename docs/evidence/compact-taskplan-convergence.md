# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. The latest Amazon,
Collections and Review PR diagnostics all stopped before approval or execution.
No business oracle passed. The gated 18-run comparison against `56942f5` has not
started; checkpoints, scoped reviews and CI are not business acceptance.

## Latest frozen diagnostics

Source `29ffd813925a35e7973c57a4ea0e04b5297fb889`, cohort `compacta29ffd813`, used
`gpt-5.5-2026-04-24` with medium reasoning throughout. Navigation target/ceiling was
3k/12k with 16,384 output tokens; drafting 8k/16k with 8,192 output tokens; Judges
4k/8k with 4,096 output tokens. The ordinary 64-call, correction, graph, cumulative
and financial allowances stayed unchanged. Source, settings, MCP binary, input
and oracle hashes are retained in the [aggregate evidence](compact-taskplan-29ffd813.json).

| Scenario | Calls / receipts / corrections | Input / output / reasoning tokens | Known cost | Recorded duration | Outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| Amazon | 9 / 9 / 0 | 13,984 / 13,047 / 11,774 | €0.409962 | 191.957 s planning | Tool-metadata clarification after a speculative prerequisite; no checkpoint |
| Collections | 39 / 39 / 4 | 62,306 / 28,324 / 22,546 | €1.031947 | 519.881 s run | Eight checkpoints; duplicate reusable calls, then repeated correction stopped |
| Review PR | 46 / 46 / 1 | 139,260 / 33,759 / 25,433 | €1.518768 | 620.966 s planning | Four checkpoints; oversized local Judge rejected before dispatch |

The 94 receipts cost €2.9606771527592641962143428417. None was empty and no new
uncertain call was created. Reasoning is included in output tokens, not added to
it. Completed-call latency was respectively 183.524, 497.994 and 579.992 seconds;
these sums exclude orchestration time. Runtime latency is unavailable because no
workflow ran.

Amazon's relevant read capability remained uninspected while only the close
contract was inspected. Collections' completion review obtained original task
and scope evidence before correctly identifying three identical reusable calls;
the export consumed only the second. Its earlier selection and local-review
contexts lacked useful accepted sibling detail. These observations support generic
presentation changes, but do not prove why the model chose repeated work.

Review PR's local Judge required 24,872 complete-request bytes / 8,547 estimated
tokens and was rejected against its captured 8,000-token ceiling. Its large typed
literal schema and verification data were not duplicated context. Independent
inspection also found a contract mismatch risk: the selected operation declared
adaptive commands unavailable under host policy while the candidate requested
command execution. This was not a Judge finding; that Judge was never dispatched.

All 1,079,644 bytes across 95 constructed requests reconcile with their components.
Every dispatched request met its captured limit; the rejected Judge remains in
these distributions. Conservative estimates are not billed provider usage.

| Phase | Requests | Complete bytes p50 / p95 / max | Estimated tokens p50 / p95 / max |
| --- | ---: | ---: | ---: |
| Intent | 7 | 3,129 / 5,318 / 5,318 | 1,299 / 2,029 / 2,029 |
| Selection | 19 | 8,266 / 14,733 / 14,733 | 3,012 / 5,167 / 5,167 |
| Discovery | 31 | 10,026 / 18,554 / 19,873 | 3,598 / 6,441 / 6,881 |
| Generation | 14 | 12,458 / 45,372 / 45,372 | 4,409 / 15,380 / 15,380 |
| Local Judge | 17 | 8,443 / 24,872 / 24,872 | 3,071 / 8,547 / 8,547 |
| Intermediate Judge | 4 | 7,914.5 / 11,850 / 11,850 | 2,894.5 / 4,206 / 4,206 |
| Correction | 3 | 16,132 / 42,682 / 42,682 | 5,634 / 14,484 / 14,484 |

The evidence uses median p50 and nearest-rank p95, retaining phase component bytes,
provider usage, costs, latency and hash indexes without repeating individual calls.
Changes `c447559` and `3efb77e` add contract-grounded substep guidance and bounded
accepted-task navigation. Their 613 Compact regressions pass; they have no live
business-success evidence yet. No runtime, authority or ceiling was changed.

## Campaign accounting and retained uncertainty

The post-diagnostic encrypted audit verifies 1,046 request identities / 1,036
receipts. All ten receiptless identities have permanent closures; the journal
separately counts nine attempts with unknown usage. No reservation was released.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 29.690940193726117479783168928 |
| Unknown-usage reservations | 3.6010530525193281791522260728 |
| Spending/reservation upper bound | 33.291993246245445658935395001 |
| Available allowance | 206.738110586460858883509552299 |

Audit evidence: `7644a182ae1a4636653794de6e44b6ef80b46846b1167f85d5e2a5da3ab3ac79`.
The separately authorized abandonment of prior Collections request 11 was applied
on October 22, retaining its full €0.2894872478450191060161734648 reservation as
unknown usage. Closure `e9a20e2d569eb75cb5c1bb7e5692bf95e1af4ea051b39b9b7f0423d9ce31fb80`
forbids replay and continuation. Its original [3adc recovery archive](compact-taskplan-3adc335.json)
remains unchanged as the earlier, pre-authorization snapshot.

Separate lost historical exposure of €159.96989616729369545755505266 remains
included within the original €400 authorization. The combined exposure upper bound
is €193.261889413539141116490447661; this is not verified actual spending. No ceiling
increased. Further paid dispatch requires a fresh balance and provider-readiness check.

## Validation and remaining acceptance

The interrupted-process operator passed 495 retention/campaign tests with warnings
as errors. The fresh presentation changes passed 613 Compact tests. The exact-head
[`29ffd813` CI](https://github.com/GnouGo/GnouGo/actions/runs/39811045971) was still running
at this snapshot; final package, Native AOT and host results are not yet claimed.
The prior [`3adc335` CI](https://github.com/GnouGo/GnouGo/actions/runs/39532215780) passed
6,331 selected-suite tests, one separate published Git test, five Flow packages,
benchmark build and Linux Native AOT, with one browser opt-in skip and no compiler
warnings. Its local full-solution and published-storage logs did not survive;
selected-project CI does not establish those lost local outcomes.

All three independent business oracles and final broad validation remain required.
Collections must execute both ordered, duplicate-preserving variants with reusable
parallel structure; Amazon needs complete observations, exact workbook contents
and browser closure; Review PR needs pinned provenance, actual checks, local
reports and cleanup. Artifact-bound review and individual runtime confirmations
remain mandatory. Only after all diagnostics pass may the 18 alternating measured
runs begin, with frozen arm settings and their differences disclosed.

## Immutable evidence

The [29ffd aggregate](compact-taskplan-29ffd813.json) indexes retained detailed
metrics and encrypted records. Previous [3adc](compact-taskplan-3adc335.json),
[4a43](compact-taskplan-4a43ea1.json), [8952](compact-taskplan-8952a0a.json),
[743](compact-taskplan-743e4a3.json), [410](compact-taskplan-41077d0.json),
[663](compact-taskplan-663cf72.json), [1984](compact-taskplan-1984bdc.json),
[afc](compact-taskplan-afc342c.json), [5c](compact-taskplan-5c01f98.json),
[08ad](compact-taskplan-08ad5db.json) and [cd174ea](compact-taskplan-cd174ea.json)
archives retain their original failures, accounting and validation limitations.
[October 19](compact-taskplan-live-2026-10-19.md),
[October 20](compact-taskplan-live-2026-10-20.md) and
[historical components](compact-taskplan-request-components-2026-10-20.json)
remain unchanged. Original payloads stay encrypted; these reports contain safe
metadata and grant no execution authority.
