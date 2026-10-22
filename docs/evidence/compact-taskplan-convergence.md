# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. The latest
Collections and Review PR diagnostics stopped before execution. An earlier source passed both
Collections runtime variants; Amazon and Review PR have not completed their
independent business checks. The 18 alternating baseline/candidate runs against
`56942f5` remain unstarted.

## Latest frozen diagnostic

Source `92d8813cffa64b1c8ed555c95a8a32c02e570dbb`, cohort `compacta92d8813`,
retained pinned `gpt-5.5-2026-04-24`, medium reasoning, 64 calls and four
corrections. Input targets/ceilings stayed 3k/12k for navigation, 8k/16k for
drafting and 4k/8k for review, with respective output reservations of
16,384/16,384/4,096. Source changed; the preceding profile's settings remained
frozen.

Collections stopped at intent request 2 after **HTTP 500 without a completion
receipt or verified usage**. The first call completed; neither call reached a
checkpoint, artifact or execution. The original inspection snapshot records the
campaign block and pending authorization. Following exact user authorization,
permanent retention was applied at **2026-10-22 20:29:09 UTC**. The full
**€0.4901093041855505198613703012** reservation and unknown-usage classification
remain; replay and continuation are permanently forbidden. No receipt or provider
termination is inferred. A distinct archive follow-up preserves this transition.

Fresh Review PR run `compacta92d8813-code-1` launched at 20:29:49 UTC on the
same frozen source and stopped after **15 receipted calls and four corrections**,
without a checkpoint, artifact or execution. Receipt inspection observed an
unsupported absence-of-capability claim while provider pages remained, followed
by four responses combining step selection and prose evidence requests. The
single-action guard rejected those responses. The version-3 selection guidance
had lost its one-action/other-fields-null instruction, while its evidence schema
still accepted prose strings. The next correction restores the instruction and
reuses drafting's exact host-navigation choices for selection and reselection,
preserving issued historical schemas. Live validation remains pending. The
capability-absence claim was a model inference despite available navigation,
so it does not justify a provider-navigation change. There were no empty
responses or new uncertain reservations. Amazon was not
retried with unchanged extraction after its unresolved, fully receipted exhaustion.

Known usage is 377 input and 310 output tokens, including 188 reasoning tokens,
costing **€0.0099395716697769483693237359**. Including the unknown reservation,
the run's upper bound is **€0.5000488758553274682306940371**. Planning elapsed
314.924 seconds; the completed call took 8.014818 seconds and the uncertain
attempt 304.597519 seconds. Unknown provider usage is not counted as zero.
Both requests fit captured ceilings, with **10,610 bytes** reconciling to
components. Complete-request p50/p95/maximum were **5,305 / 8,198 / 8,198 bytes**;
conservative estimates were 2,024.5 / 2,989 / 2,989 tokens. Estimates are not
billed usage. Useful-obligation ratios and runtime latency remain unavailable.
The [92d8813 archive](compact-taskplan-92d8813.json) retains the freeze,
measurements, exact request/inspection hashes, accounting and Planning validation.

Review PR used 40,948 input and 13,631 output tokens, including 8,210 reasoning
tokens, costing **€0.5453390207055896205456322758**. Planning took 231.258 seconds;
completed calls totalled 210.023731 seconds. All 15 requests fit their phase
ceilings and **185,660 bytes** reconcile with components. Complete-request
p50/p95/maximum were **11,146 / 27,888 / 27,888 bytes**; conservative estimates
were 3,972 / 9,552 / 9,552 tokens. No measured business improvement is established.

## Previous diagnostics and executed result

The [407243a archive](compact-taskplan-407243a.json) retains a fully receipted
Collections failure: 17 calls, four corrections, three checkpoints, 27,290 input
and 13,013 output tokens (9,049 reasoning), 238.408 seconds and
€0.4681773749222429574335732694. Receipt inspection exposed inconsistent
prerequisite-reference authority. Revision `92d8813` shares citation eligibility
between presentation and validation without widening coverage or closure
permissions. Its fresh run stopped before exercising this correction live.

The unchanged [bbd900d9 archive](compact-taskplan-bbd900d9.json) retains four
failed diagnostics: Collections with two separately frozen output profiles,
Amazon and Review PR. All 42 calls had receipts, using 82,489 input and 63,465
output tokens, including 53,429 reasoning tokens, and costing
€2.0584688527503776770638940727. Planning totalled 993.191 seconds. No run executed.
Profile B changed only draft/correction output from 8,192 to 16,384 after profile
A's fully receipted exhaustion. Two responses had exhausted output with no
visible content; their cause remains unproved. Readiness declares `medium`,
`high` and `max`; `low` is unsupported and was not tested. No issued request
changed or was replayed.

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

The audit after Review PR verifies **1,234 requests / 1,222 receipts**. All twelve
receiptless identities have permanent closures; the journal separately counts
eleven unknown attempts. Closure released no reservation; Review PR added only
verified usage.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 37.574797831689327290500311018 |
| Unknown-usage reservations | 4.5812716608904292188749666752 |
| Spending/reservation upper bound | 42.156069492579756509375277693 |
| Available allowance | 197.874034340126548033069669607 |

Audit evidence: `819a8949c608d129a9dfc8e07d0fe86a11a273b9eae9e6cf76d923ea1c97282e`.
Separate lost historical exposure of €159.96989616729369545755505266 remains
accounted inside the original €400 authorization. Including that exposure, the
cumulative spending/reservation upper bound is approximately **€202.125966**.
Exact audited decimal strings are retained; independently summed run totals can
differ from accumulated ledger totals at the last retained decimal places.
Further dispatch must recheck balance and readiness; these are snapshot values,
not new allowances.

## Validation and remaining acceptance

All **2,494 Planning tests passed with warnings as errors** in isolation, with
source paths verified against `92d8813`; 68 focused tests also passed. The
archive retains the exact TRX and validation hashes. Earlier correction-accounting
test failures were resolved while preserving candidate findings and accounting.
The subsequent selection-navigation correction passes **2,497 Planning tests**
and **71 focused tests** with warnings as errors. Its benchmark build is warning-free;
independent review found no authority or historical-recovery defect. This is
deterministic validation, with fresh live validation still pending.
Full-solution and final-revision CI results remain pending. Deterministic tests
establish no live business acceptance.

On exact isolated `92d8813`, all eleven publication-validation stages passed:
independent Core, Planning, Integrations, Copilot and Persistence packages;
Native AOT planning publish/smoke; trimmed Server publish/encrypted-storage
smoke; and Git MCP Native AOT publish/published-contract test. Builds used
warnings as errors with existing documented warning policies unchanged. All
eleven log hashes, eight artifact hashes/sizes and the Git TRX were verified.
Server publication excluded frontend and bundled-tool publication. The archive's
separate publication follow-up retains those boundaries and exact evidence.
Earlier `bbd900d9` evidence remains unchanged; neither check set establishes
live business acceptance or validation of subsequent source changes.

Collections must pass again on the final candidate. Amazon requires complete
observations, product identities, workbook contents and browser closure. Review
PR requires pinned provenance, actual commands, local reports and cleanup,
including independent assessment of the requested diff review. Artifact review
and exact individual confirmations remain mandatory. Only after all three
diagnostics pass may the 18 paired runs begin. Full-solution warnings-as-errors,
independent packages, AOT, published encrypted-storage and CI checks remain
required for the delivered revision.

## Immutable evidence

The [92d8813 archive](compact-taskplan-92d8813.json) indexes the latest uncertain
Collections request, unchanged preclosure inspection, separate authorized
closure/readiness follow-up, the subsequent receipted Review PR failure and exact
revision publication validation.
The [407243a archive](compact-taskplan-407243a.json) retains the preceding receipted
Collections failure. The [bbd900d9 archive](compact-taskplan-bbd900d9.json) retains
four earlier failures and published validation; the
[bb5b726 archive](compact-taskplan-bb5b726.json) retains prior failures, exact
permanent abandonment with its full reservation, and validation. Earlier
[efcb](compact-taskplan-efcb1d7a.json), [d488](compact-taskplan-d488720a.json),
[29ffd](compact-taskplan-29ffd813.json), [3adc](compact-taskplan-3adc335.json),
[4a43](compact-taskplan-4a43ea1.json), [8952](compact-taskplan-8952a0a.json),
[743](compact-taskplan-743e4a3.json), [410](compact-taskplan-41077d0.json),
[663](compact-taskplan-663cf72.json), [1984](compact-taskplan-1984bdc.json),
[afc](compact-taskplan-afc342c.json), [5c](compact-taskplan-5c01f98.json),
[08ad](compact-taskplan-08ad5db.json) and [cd174ea](compact-taskplan-cd174ea.json)
remain unchanged. [October 19](compact-taskplan-live-2026-10-19.md),
[October 20](compact-taskplan-live-2026-10-20.md) and
[historical components](compact-taskplan-request-components-2026-10-20.json)
preserve earlier protocols and failures. Original payloads remain encrypted;
safe reports grant no execution authority.
