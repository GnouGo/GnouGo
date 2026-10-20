# Compact TaskPlan convergence

Business acceptance remains incomplete. PR #120 stays Draft. No completed live
Amazon, Collections or Review PR execution is claimed by the retained diagnostic
results below. Compilation, scoped reviews and accepted checkpoints are separate
measurements.

## Latest retained diagnostic

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

The next candidate starts from `afc342c` with a captured phase policy: navigation,
draft and Judge targets remain 3k/8k/4k, while hard complete-request ceilings are
6k/16k/8k. Initial reasoning stays medium and output reservations stay
8,192/8,192/2,048. It also removes avoidable import-selection and evidence-charging
loops, uses shared selected-kind schemas, admits coherent evidence batches and
constrains Judge references to supplied objects.

The configured pinned deployment declares `medium`, `high` and `max` reasoning,
but not `low`. Read-only configuration and metadata hashes still match the
retained model identity. The proposed low-navigation experiment is therefore
unavailable without a separately authorized configuration change; no such change
or model substitution was made.

These changes are not yet live acceptance evidence. Fresh diagnostics must execute
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
| Known spending | 15.517235403892295387896560922 |
| Retained unknown-usage reservations | 0.4953212476672887230071980805 |
| Spending/reservation upper bound | 16.012556651559584110903759002 |
| Available allowance | 224.017547181146720431541188298 |

Evidence hash:
`1bffca0a4adf681865093143051951c808cec794bf33ed0a19c9992d7437925e`.
There are 597 reserved request identities and 594 receipts. Three receiptless
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
