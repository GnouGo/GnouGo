# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. The latest
diagnostics stopped before execution. An earlier source passed both
Collections runtime variants; Amazon and Review PR have not completed their
independent business checks. The 18 alternating baseline/candidate runs against
`56942f5` remain unstarted.

## Latest frozen diagnostics

Source `bbd900d956735dc09bc6366c8bcc7fd00830f05a` used the pinned
`gpt-5.5-2026-04-24`, medium reasoning, 64 calls and four corrections. Input
targets/ceilings stayed 3k/12k for navigation, 8k/16k for drafting and 4k/8k for
review. Profile A reserved 16,384/8,192/4,096 output tokens respectively.
After its fully receipted draft exhaustion, separately frozen profile B changed
only draft/correction output to 16,384. No issued request changed or was replayed.

The [safe archive](compact-taskplan-bbd900d9.json) indexes exact freezes, detailed
safe measurements, receipts by hash and published validation. All 42 calls have
completion receipts; these diagnostics added no unknown-usage reservation.

| Profile / scenario | Calls | Corrections | Input / output / reasoning tokens | Planning seconds | Cost EUR | Outcome |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| A / Collections | 4 | 0 | 5,020 / 10,672 / 9,864 | 147.536 | 0.306816 | First draft exhausted output; no visible response |
| B / Collections | 21 | 4 | 34,184 / 21,273 / 17,550 | 359.140 | 0.719017 | Four checkpoints; selection rejected and correction allowance exhausted |
| B / Amazon | 2 | 0 | 2,217 / 17,691 / 17,408 | 248.658 | 0.481485 | Interface extraction exhausted output; no visible response |
| B / Review PR | 15 | 4 | 41,068 / 13,829 / 8,607 | 237.857 | 0.551151 | Discovery reached; selection rejected and correction allowance exhausted |

Profile A is cohort `compactabbd900d9`; profile B is `compactbbbd900d9`.
None produced an approved artifact or executed. Checkpoints do not establish
business acceptance; calls per independently accepted business obligation and
runtime latency remain unavailable. Two responses exhausted output with no
visible content. Their cause remains unproved. Readiness declares `medium`,
`high` and `max`; `low` is unsupported and was not tested.

The four runs cost **€2.0584688527503776770638940727**, using 82,489 input and
63,465 output tokens, including 53,429 reasoning tokens. Planning totalled
993.191 seconds; completed model calls totalled 950.272154 seconds. All 42
requests fit captured ceilings and **416,756 bytes** reconcile with components.
Complete-request p50/p95/maximum were **9,219.5 / 20,300 / 25,946 bytes**;
conservative input estimates were 3,329.5 / 7,023 / 8,905 tokens. Percentiles
include all prepared requests; component estimates are not billed usage.
These diagnostic profiles establish neither improved reliability nor an
architecture-only comparison.

The earlier [bb5b726 archive](compact-taskplan-bb5b726.json) remains unchanged,
including the interrupted Collections request, its exact permanent abandonment
and full reservation, and the separately receipted extraction exhaustion.

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

The latest public-API audit verifies **1,200 requests / 1,189 receipts**. All eleven
receiptless identities have permanent closures; the journal separately counts ten
unknown attempts. No reservation was released.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 36.551341864391717764151781738 |
| Unknown-usage reservations | 4.0911623567048786990135963740 |
| Spending/reservation upper bound | 40.642504221096596463165378112 |
| Available allowance | 199.387599611609708079279569188 |

Audit evidence: `ea7d6f21ba386b968f3740f753516088b95c3ecd5c6fce19e78889b6d809277c`.
Separate lost historical exposure of €159.96989616729369545755505266 remains
accounted inside the original €400 authorization. Including that exposure, the
cumulative spending/reservation upper bound is approximately **€200.612400**.
Exact audited decimal strings are retained; independently summed run totals can
differ from accumulated ledger totals at the last retained decimal places.
Further dispatch must recheck balance and readiness; these are snapshot values,
not new allowances.

## Validation and remaining acceptance

On isolated `bbd900d9`, independent Core, Planning, Integrations, Copilot and
Persistence package builds passed with warnings as errors. Native AOT planning,
trimmed published encrypted-storage smoke, Git MCP Native AOT publication and
its published contract test also passed. Server publication excluded frontend
builds and bundled-tool publication; existing documented warning policies were
unchanged. The archive records exact binary/log hashes and validation boundaries.
Full-solution and final-revision CI results remain pending; no green result or
business success is inferred from these checks.

Collections must pass again on the final candidate. Amazon requires complete
observations, product identities, workbook contents and browser closure. Review
PR requires pinned provenance, actual commands, local reports and cleanup,
including independent assessment of the requested diff review. Artifact review
and exact individual confirmations remain mandatory. Only after all three
diagnostics pass may the 18 paired runs begin. Full-solution warnings-as-errors,
independent packages, AOT, published encrypted-storage and CI checks remain
required for the delivered revision.

## Immutable evidence

The [bbd900d9 archive](compact-taskplan-bbd900d9.json) indexes the latest four
receipted failures, phase measurements, accounting and published validation.
The [bb5b726 archive](compact-taskplan-bb5b726.json) retains prior failures,
permanent abandonment and validation. Earlier [efcb](compact-taskplan-efcb1d7a.json),
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
