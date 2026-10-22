# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. None of the three
latest workflows reached execution. Checkpoints, scoped reviews and green CI do
not establish business success. The gated 18-run baseline/candidate comparison
has not started.

## Latest frozen diagnostics

Source `3adc3351dec2748d9abf5eebb7e1986cbdf3762a`, cohort `compacta3adc335`,
used the pinned model with medium reasoning: navigation target/ceiling 3k/12k
with 16k output, drafting 8k/16k with 8k output, and Judge 4k/8k with 4k output.
The 64-call allowance and correction/graph/financial limits were unchanged.

| Scenario | Calls / receipts / corrections | Known input / output / reasoning tokens | Known cost | Outcome |
| --- | ---: | ---: | ---: | --- |
| Amazon | 2 / 1 / 0 | 507 / 1,240 / 961 | €0.035311 | Uncertain transport at intent request 2; exact abandonment authorized and applied |
| Collections | 11 / 10 / 1 | 14,358 / 7,744 / 6,134 | €0.270248 | One checkpoint; interrupted during correction request 11, with no outcome recorded |
| Review PR | Not dispatched | — | — | No outcome |

No workflow executed. Missing receipts are unknown outcomes, not empty responses;
all completed receipts contain visible content. Collections' local Judge correctly
rejected completing the collection requirement after only its scalar reusable
body had been built. The subsequent correction and review retained the omission;
the pending correction's outcome is unknown.

All 94,643 Collections request bytes and 8,143 Amazon request bytes reconcile.
No captured ceiling was exceeded. Collections' largest request was 14,252 bytes /
5,007 estimated tokens; its largest Judge was 8,877 bytes / 3,215 estimated tokens.
Amazon recorded 321.909 seconds. Collections has only 121.124 seconds of completed
call latency and a 128.198-second active-time snapshot; total planning latency is
incomplete. [Recovered safe metrics](compact-taskplan-3adc335.json) retain phase
p50/p95/maxima, component sizes, usage and recovery limitations. Component estimates
are not billed provider tokens.

The preceding [4a43 cohort](compact-taskplan-4a43ea1.json) retained 49 known receipts,
€1.6266373412 cost and no execution. Amazon stopped on duplicate export-schema
context, Collections on a premature completion claim and repeated correction,
and Review PR on cumulative command evidence exceeding its 8k navigation ceiling.
The focused `3adc335` changes reduce unrelated export-schema context and explain
existing reselection for premature closure. The larger navigation ceiling and
source changes remain confounded; no completed business outcome validates their
benefit. The 18-run comparison remains gated on all three business oracles.

## Campaign accounting and retained uncertainty

The recovered read-only audit verifies 952 request identities / 942 receipts.
Nine receiptless identities are permanently retained; Collections request 11 is
unclosed. The ledger separately counts nine attempts with unknown usage. All
unknown usage remains reserved.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 26.730263040966853283568826090 |
| Unknown-usage reservations | 3.6010530525193281791522260728 |
| Spending/reservation upper bound | 30.331316093486181462721052163 |
| Available allowance | 209.698787739220123079723895137 |

Audit evidence: `4970ffa98ef383d2bb152f5b6c9965804d9df70e1dfabcddc5450557f2fe4255`.
Amazon's authorized abandonment preserves its full €0.4901093041855505198613703012
reservation. Collections retains €0.2894872478450191060161734648 for its pending
request. A bare HTTP reservation does not establish whether dispatch completed;
no receipt, failure or provider termination may be fabricated. The benchmark's
explicit interrupted-process operator preserves the original generating session
and adds only a separately authorized permanent closure. Its exact operation
remains unapplied pending authorization.

Prior [743](compact-taskplan-743e4a3.json) and [410](compact-taskplan-41077d0.json)
records remain unchanged. Abandoned requests and runs cannot be replayed or
continued. Separate lost historical exposure of €159.96989616729369545755505266
remains included within the original €400 authorization; no ceiling increased.

## Validation and remaining acceptance

[CI for head `3adc335`](https://github.com/GnouGo/GnouGo/actions/runs/39532215780)
passed through GitHub's PR merge checkout: 6,331 selected-suite tests passed,
including 2,392 Planning tests, with one browser opt-in skipped. One additional
published Git test, five Flow packages, benchmark build and Linux Native AOT
publish/execution passed; no compiler warnings. The safe recovered CI result hash
is `44a60e53dad9ed91a535f322122c9742c54cd9ab9b22b5007f47c2ef839d5e74`.

The exact local full-solution completion log was lost with temporary files;
selected-project CI does not prove that command completed. The previously reported
published encrypted-storage smoke also lacks a surviving local log. Original
requests, contracts and receipts survived in encrypted storage, and safe recovery
evidence is now retained outside temporary directories.

The earlier complete solution at `8952a0a` passed 7,329 tests with 13 skips and no
failures or warnings. Its historical result hash is
`eca8d7cd4384953c4a5516a48f30ad6254a7801e90eb6d362b876affdf54973f`.
The new interrupted-process operator passed 495 focused retention/campaign tests
with warnings as errors, including encrypted restart, unchanged reservations,
stale evidence, outcome rejection and permanent replay/continuation denial.

Complete final validation and all three independent business oracles remain
required. Collections must execute both ordered, duplicate-preserving variants
with reusable parallel structure. Amazon v4 requires complete observations, exact
products/workbook data and browser closure. Review PR requires pinned provenance,
actual command results, independent report assessment and cleanup. Artifact-bound
review and individual runtime confirmations remain mandatory. Only then may the
18 alternating runs against `56942f5` begin, with disclosed arm settings.

## Immutable evidence

[8952 controls and phase experiments](compact-taskplan-8952a0a.json) retain all
five runs and 117 receipts (SHA-256
`aabd4815978e8be0f56d1f8d2e7f82626d5b97308acd358de841aef43fbd8ca6`).
Earlier [663](compact-taskplan-663cf72.json), [1984](compact-taskplan-1984bdc.json),
[afc](compact-taskplan-afc342c.json), [5c controls](compact-taskplan-5c01f98.json),
[08ad](compact-taskplan-08ad5db.json) and [cd174ea](compact-taskplan-cd174ea.json)
archives retain their original failures, balances and validation limitations.

| Archive | SHA-256 |
| --- | --- |
| [October 19](compact-taskplan-live-2026-10-19.md) | `f270b2b0f96ca6930d9f8c603153f6a1fbe0bfa1519309603998f71ad6b1f7e6` |
| [October 20 through `0e76414`](compact-taskplan-live-2026-10-20.md) | `2853a1c5bf337a25826a58b6b22ff88b32b009c492cd19f3b4dbb3a2c3f690db` |
| [Historical components](compact-taskplan-request-components-2026-10-20.json) | `48fd3dab8c923e6c307445d1a99295cfea554227f8df015b5eddb5ba2d34c286` |

Original prompts, contracts, requests and receipts remain encrypted. These reports
contain safe metadata and grant no execution authority.
