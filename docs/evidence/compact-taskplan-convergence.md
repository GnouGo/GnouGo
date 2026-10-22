# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. The latest
Collections diagnostics stopped before execution. An earlier source passed both
Collections runtime variants; Amazon and Review PR have not completed their
independent business checks. The 18 alternating baseline/candidate runs against
`56942f5` remain unstarted.

## Latest frozen diagnostics

Source `bb5b726506754d8d4cfbbd01023d43e9003547f1` used the pinned
`gpt-5.5-2026-04-24`, medium reasoning, 64 calls and four corrections. Navigation
target/ceiling/output were 3k/12k/16,384; drafting 8k/16k/8,192; review 4k/8k/4,096.
The [safe archive](compact-taskplan-bb5b726.json) retains exact run, request,
receipt, closure, settings and validation identities.

| Collections cohort | Calls / receipts | Verified input / output / reasoning tokens | Known cost | Unknown reservation | Outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| `compactabb5b726` | 2 / 1 | 377 / 425 / 324 | €0.013005 | €0.490109 | Interrupted request 2; exact permanent abandonment applied |
| `compactbbb5b726` | 2 / 2 | 1,911 / 16,999 / 16,896 | €0.461677 | €0 | Extraction exhausted output; no visible response |

The interrupted request retains its full **€0.4901093041855505198613703012**
reservation and unknown usage. Closure evidence
`8fcc0cf1adb2ceb370870e7b99f34bbefd63985cd89cacbc13a9b350f64c8959` permanently
forbids replay and continuation; it does not establish provider termination.

The b-cohort was a fresh independent run. Its second receipt records 1,534 input
tokens, 16,384 reasoning/output tokens and zero visible content. Planning took
288.039 seconds; completed-call stages totalled 285.984403 seconds. Complete
original numeric-array input/output context was supplied. Output exhaustion does
not prove its cause. Neither run accepted a checkpoint or produced an artifact;
calls per independently accepted business obligation remain undefined. Amazon
and Review PR were not started in these two cohorts.

All four requests fit the captured ceiling and their **21,089 bytes** reconcile
with components. Complete-request p50/p95/maximum were 5,272 / 8,133 / 8,133 bytes;
conservative input estimates were 2,013.5 / 2,967 / 2,967 tokens. Estimates are not
billed usage; provider reasoning is included in output tokens.

The next correction restores type/default semantics removed from extraction
guidance by schema compaction. This demonstrated presentation defect has no live
convergence proof yet. Readiness declares `medium`, `high` and `max` reasoning
support; `low` is unavailable and was not tested. Issued settings remain frozen.

## Earlier executed Collections result

Source `efcb1d7aebc2de01154e946a749e66b83a5813ae`, cohort `compactaefcb1d7a`,
passed both real Collections runtime variants after independent artifact-bound
`delegated_test` review. Exact results, order, duplicates and result-contributing
parallel reusable calls passed; effects and safety violations were empty and no
runtime LLM was required. The run used 42 calls, three corrections, 81,886 input
and 38,908 output tokens, 655.835 seconds of planning and €1.401111. Per-variant
runtime timing was unavailable. Capacity beyond 100 items and extreme numerical
ranges remain unverified.

Amazon stopped after nine receipted calls when descriptor navigation was lost;
Review PR stopped after four receipted calls during interface extraction. Neither
executed. The unchanged [efcb archive](compact-taskplan-efcb1d7a.json) preserves
all 55 receipts, phase distributions, €2.1383764329512130098640362567 cost and
execution evidence. That success does not establish repeatability or acceptance
of the new source.

## Campaign accounting

The latest public-API audit verifies **1,158 requests / 1,147 receipts**. All eleven
receiptless identities have permanent closures; the journal separately counts ten
unknown attempts. No reservation was released.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 34.492873011641340087087887666 |
| Unknown-usage reservations | 4.0911623567048786990135963740 |
| Spending/reservation upper bound | 38.584035368346218786101484040 |
| Available allowance | 201.446068464360085756343463260 |

Audit evidence: `3b359f2bad877846f15daa687c4d66029bb3f2fbeb9a1d80b7127c0f0084ef18`.
Separate lost historical exposure of €159.96989616729369545755505266 remains
accounted inside the original €400 authorization. Further dispatch must recheck
balance and readiness; these are snapshot values, not new allowances.

## Validation and remaining acceptance

Isolated `bb5b726` Native AOT planning and published encrypted-storage smoke checks
passed on osx-arm64. Server publication excluded frontend builds, bundled tool
publication and model metadata regeneration. This does not establish full-solution
or independent package-build validation.

[CI run 39836074523](https://github.com/GnouGo/GnouGo/actions/runs/39836074523)
failed on two stale `QueuedPlanningSessionCompatibilityTests` expectations. The
isolated original Planning suite reproduced 2,473 passes / two failures. The
fixture-only fix `50d9a91d5c7e614b7935813e79dc89b4185b1775` passed all **2,475**
Planning tests with warnings as errors. Exact revisions and logs are archived;
this does not establish final-revision CI or business success.

Collections must pass again on the final candidate. Amazon requires complete
observations, product identities, workbook contents and browser closure. Review
PR requires pinned provenance, actual commands, local reports and cleanup,
including independent assessment of the requested diff review. Artifact review
and exact individual confirmations remain mandatory. Only after all three
diagnostics pass may the 18 paired runs begin. Full-solution warnings-as-errors,
independent packages, AOT, published encrypted-storage and CI checks remain
required for the delivered revision.

## Immutable evidence

The [bb5b726 archive](compact-taskplan-bb5b726.json) indexes the latest failures,
retention and validation. Earlier [efcb](compact-taskplan-efcb1d7a.json),
[d488](compact-taskplan-d488720a.json), [29ffd](compact-taskplan-29ffd813.json),
[3adc](compact-taskplan-3adc335.json), [4a43](compact-taskplan-4a43ea1.json),
[8952](compact-taskplan-8952a0a.json), [743](compact-taskplan-743e4a3.json),
[410](compact-taskplan-41077d0.json), [663](compact-taskplan-663cf72.json),
[1984](compact-taskplan-1984bdc.json), [afc](compact-taskplan-afc342c.json),
[5c](compact-taskplan-5c01f98.json), [08ad](compact-taskplan-08ad5db.json) and
[cd174ea](compact-taskplan-cd174ea.json) remain unchanged.
[October 19](compact-taskplan-live-2026-10-19.md),
[October 20](compact-taskplan-live-2026-10-20.md) and
[historical components](compact-taskplan-request-components-2026-10-20.json)
preserve earlier protocols and failures. Original payloads remain encrypted;
safe reports grant no execution authority.
