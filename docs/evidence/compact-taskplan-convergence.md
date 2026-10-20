# Compact TaskPlan convergence

Business acceptance remains incomplete. PR #120 stays Draft. No completed live
Amazon, Collections or Review PR execution is claimed by the retained diagnostic
results below. Compilation, scoped reviews and accepted checkpoints are separate
measurements.

## Latest controls and phase-output experiments

Source `5c01f98e803bc06bd0f241bd2491c1200fef3c77` kept medium reasoning, 3k/8k/4k input targets and 6k/16k/8k hard ceilings. The controls retained navigation/draft/Judge outputs of 8,192/8,192/2,048.

| Run | Calls / corrections / checkpoints | Known cost | Observed result |
| --- | ---: | ---: | --- |
| Amazon control | 2 / 0 / 0 | €0.278948 | Interface extraction exhausted 8,192 reasoning tokens with zero visible output |
| Collections control | 9 / 1 / 1 | €0.423287 | Draft call 9 exhausted 8,192 reasoning tokens with zero visible output |
| Review PR control | 3 / 0 / 0 | €0.285626 | Interface extraction exhausted 8,192 reasoning tokens with zero visible output |
| Collections, draft output 16,384 | 2 / 0 / 0 | €0.234933 | Unchanged navigation exhausted 8,192 tokens; no draft used the changed allowance |
| Amazon, navigation output 16,384 | 10 / 1 / 0 | €0.298765 | Discovery repeated a rejected evidence batch: 6,759 estimated input tokens against 6,000 |

The three controls total 14 known calls, 540.344 seconds and **€0.9878610148404869812494445923**; all 76,805 request bytes reconcile, with no input-limit rejection, new uncertainty or execution. Provider usage was 13,456 input and 34,812 output tokens, including 32,825 reasoning tokens.

The draft-output experiment never reached its changed phase and cannot establish whether 16,384 helps drafting. The separate navigation trial preserved source, model, input ceilings, call/correction limits, MCPs and oracles. All its responses completed below 8,192 output tokens, so no causal benefit from the larger allowance is established. It used 19,096 input / 8,024 output / 6,259 reasoning tokens in 125.430 seconds, with no empty response or new uncertainty. Empty output establishes exhaustion, not its cause. These fresh trials do not resume or replay a stopped request.

Business acceptance remains incomplete; there is no verified complete execution and the final 18-run comparison has not started. Retain the [663cf72 measurements](compact-taskplan-663cf72.json), [1984bdc measurements](compact-taskplan-1984bdc.json) and immutable archive index as historical evidence.

[Safe phase measurements](compact-taskplan-5c01f98.json) include both separately frozen experiments and the controls, rejected requests, reconciled components and exact evidence hashes. Discovery had retained unrelated later operations while leaving the relevant description continuation unread. The follow-up replaces broad inspection guidance with descriptor shortlisting and selects the next action with bindable prerequisites. Original requirement closure, admission and repeat guards remain unchanged.

## Preceding retained diagnostics

Source `663cf727204cc0786c26783f94cb1047903bbede` kept the same medium profile and
output reservations. The three new runs stopped before generation or execution.

| Scenario | Calls / corrections | Time | Cost | Observed blocker |
| --- | ---: | ---: | ---: | --- |
| Amazon | 14 / 1 | 258.686 s | €0.321319 | Provider discovery discarded requested descriptions; the next call repeated their batch |
| Collections | 2 / 0 | 123.300 s | €0.236906 | Interface extraction exhausted 8,192 reasoning tokens without visible output |
| Review PR | 7 / 0 | 59.057 s | €0.130863 | Fetching another requirement page discarded clauses needed for selection |

All 23 calls have known receipts; the total cost was **€0.6890873544832489114014040699**.
There were no checkpoints, effects, new uncertain calls or input-limit rejections.
Complete-request components reconcile. The highest input estimate was 4,443 tokens
for discovery. [Safe phase measurements](compact-taskplan-663cf72.json) retain
request-size distributions, component totals, provider usage and evidence hashes.

Amazon passed the preceding requirement-page blocker, but both external scenarios
exposed the same remaining defect: explicitly requested context was replaced between
calls. The next correction retains that evidence within its current step and existing
complete-request admission. It does not weaken repetition detection.

Collections' extraction schema and instructions matched the preceding successful
transport response; only source segmentation differed. That earlier receipt omitted
the explicitly declared public result. Requiring a declaration in each classified
interface direction addresses the omission; it does not explain or prove a fix for
reasoning exhaustion. No unchanged failed request was retried.

## Preceding three-scenario diagnostics

Source `1984bdcc81e6434c82170885bcd0ef5578a51a90` used the frozen medium profile,
3k/8k/4k input targets, 6k/16k/8k ceilings and 8,192/8,192/2,048 output reservations.
All three runs stopped before execution; all 30 requests have known receipts.

| Scenario | Calls / corrections | Time | Cost | Observed blocker |
| --- | ---: | ---: | ---: | --- |
| Amazon | 5 / 0 | 44.478 s | €0.085355 | Selection reread a requirement page after another page replaced it |
| Collections | 15 / 2 | 249.263 s | €0.468284 | Judge confused an imported operand with a computed result; a later Judge exhausted 2,048 output tokens |
| Review PR | 10 / 0 | 98.433 s | €0.210348 | Discovery repeated an already supplied description while other provider actions were hidden |

Total known cost was **€0.7639873811428063627477117213**, with 39,331 provider
input tokens, 22,102 output tokens (including 17,985 reasoning tokens), one empty
response and no new uncertain usage. Collections accepted two checkpoints, which
do not establish a complete or executable business result.

Every complete request remained within its captured input ceiling. Peak estimates
were 4,238 for generation, 4,860 for repair and 3,040 for Judges. Component byte
sums reconcile for all 30 requests. [Safe phase measurements](compact-taskplan-1984bdc.json)
retain p50/p95/maximum sizes, component totals, receipt counts, costs and evidence
hashes. Exact requests and responses remain encrypted.

The next corrections address these observed presentation defects without relaxing
semantic validation or repeat guards: coherent adjacent original requirement
pages, explicit imported-operand labels for Judges, and bounded available provider
actions alongside inspected capability evidence. A separately frozen 4,096-token
Judge experiment is now eligible because exhaustion was observed; it has not been
dispatched. Source and settings changes must remain separately identified.

## Preceding diagnostic

Source `afc342c97e10e58ef32ea1c273cd35afd443709c`, Amazon run
`compactdafc342c-amazon-1`, stopped before request 21 was dispatched: discovery
needed 3,111 conservatively estimated input tokens against its captured 3,000
ceiling. It had 20 known calls, three corrections and two accepted checkpoints.
There was no pending call, empty response, new uncertain usage or execution.

| Measurement | Result |
| --- | ---: |
| Provider input / output / reasoning tokens | 30,825 / 11,938 / 9,231 |
| Prepared requests, including local rejection | 21 |
| Complete prepared request bytes | 160,920 |
| Planning time | 229.898 seconds |
| Known planning cost | €0.4552252732604638762996534255 |
| Independent business outcome | Incomplete; not executed |

[Safe phase measurements](compact-taskplan-afc342c.json) retain nearest-rank
p50/p95/maximum request sizes, reconciled component bytes, provider accounting,
terminal diagnostics and ledger identity. Component token estimates are not
billed usage; reasoning is part of provider output, not additional output.

## Current experiment

The simplification beginning at `1984bdc` uses a captured phase policy: navigation,
draft and Judge targets remain 3k/8k/4k, while hard complete-request ceilings are
6k/16k/8k. Control reasoning stays medium with output reservations of
8,192/8,192/2,048; the separate experiments above change one family at a time. It also removes avoidable import-selection and evidence-charging
loops, uses shared selected-kind schemas, admits coherent evidence batches and
constrains Judge references to supplied objects.

The configured pinned deployment declares `medium`, `high` and `max` reasoning,
but not `low`. Read-only configuration and metadata hashes still match the
retained model identity. The proposed low-navigation experiment is therefore
unavailable without a separately authorized configuration change; no such change
or model substitution was made.

These changes have not established live business acceptance. Fresh diagnostics must execute
Amazon, Collections and Review PR with independent oracles and exact delegated
confirmations. Amazon's new v4 oracle rejects a narrowed search subtree even when
its capture is complete. Review PR also needs an independent assessment of its
actual report against the pinned diff; command receipts alone do not prove review
quality. Historical verdicts stay unchanged, and paired comparisons apply the same
supplementary checks to both arms. Only after all three succeed can the agreed 18 alternating
baseline/candidate runs begin. Baseline is `56942f5`; each arm freezes its retained
or best validated configuration. Intentional settings differences must be reported,
so the comparison establishes configuration-level outcomes.

## Campaign reconciliation

Read-only inspection of encrypted campaign
`schema-portability-20261019-incremental` verified:

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 18.491868834977339376166355638 |
| Retained unknown-usage reservations | 0.4953212476672887230071980805 |
| Spending/reservation upper bound | 18.987190082644628099173553718 |
| Available allowance | 221.042913750061676443271393582 |

Evidence hash:
`dbc3b273d39213f0a718c127cdcf6b0a29a387cbc098c876ddb8175bf99b1265`.
There are 676 reserved request identities and 673 receipts. Three receiptless
identities are permanently retained; two have unknown-usage reservations. They
remain non-replayable and their abandoned runs cannot continue. Separate lost
historical exposure of €159.96989616729369545755505266 remains accounted for
within the original €400 authorization. No financial ceiling was increased.

## Immutable evidence index

These reports are retained archives. Their historical balances and intermediate
results are not current acceptance claims.

| Archive | SHA-256 |
| --- | --- |
| [October 19 diagnostics](compact-taskplan-live-2026-10-19.md) | `f270b2b0f96ca6930d9f8c603153f6a1fbe0bfa1519309603998f71ad6b1f7e6` |
| [October 20 diagnostics through `0e76414`](compact-taskplan-live-2026-10-20.md) | `2853a1c5bf337a25826a58b6b22ff88b32b009c492cd19f3b4dbb3a2c3f690db` |
| [Historical request components](compact-taskplan-request-components-2026-10-20.json) | `48fd3dab8c923e6c307445d1a99295cfea554227f8df015b5eddb5ba2d34c286` |

Private source text, complete contracts, exact requests and receipts remain in
encrypted storage. This summary and linked metadata contain no execution authority.
