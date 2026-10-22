# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. The latest
Collections diagnostic stopped before execution; Amazon and Review PR were not
dispatched in this cohort. An earlier source passed both Collections runtime
variants. The 18 alternating baseline/candidate runs against `56942f5` remain
unstarted.

## Latest frozen diagnostic

Source `d8f51c54c763f0caf95eec2a6206beaf9e0fdd95`, cohort `compactad8f51c54`,
retained pinned `gpt-5.5-2026-04-24`, medium reasoning, 64 calls and four
corrections. Input targets/ceilings stayed 3k/12k for navigation, 8k/16k for
drafting and 4k/8k for review, with respective output reservations of
16,384/16,384/4,096. The prior profile's settings remained frozen.

Collections reached **seven checkpoints in 35 calls with two corrections**, then
stopped at intermediate Judge call 35. That call has an HTTP 200 completion
receipt reporting `output_limit`: all 4,096 output tokens were reasoning, with
no visible response. All 35 calls have receipts; no uncertain reservation was
added. There was no completed artifact or runtime execution.

Independent inspection identified draft-only `reselectStep` and add-export
instructions appended to a Judge request whose schema permits findings and
evidence only. This prompt/schema mismatch is demonstrated; it does **not**
establish the cause of reasoning exhaustion. The next correction omits those
authoring actions from fresh Judges while preserving factual TaskPlan semantics,
draft guidance and historical requests. Model and output settings remain unchanged.
It passes 2,500 Planning tests and 65 focused tests with warnings as errors;
independent review found no authority or compatibility defect. Live validation
is pending. Neither seven
checkpoints nor deterministic validation establishes business acceptance.

The run used **65,106 input / 35,630 output / 29,953 reasoning tokens**, costing
**€1.2391628898960277259397494006**. Planning took 593.573 seconds; completed
model calls totalled 572.127043 seconds. All requests fit captured phase ceilings
and **354,453 bytes** reconcile with components. Complete-request p50/p95/maximum
were **9,672 / 14,463 / 15,660 bytes**; conservative input estimates were
3,480 / 5,077 / 5,476 tokens. Estimates are not billed usage; reasoning is included
in output. Calls per independently satisfied business obligation and runtime
latency remain unavailable. The [d8f51c5 archive](compact-taskplan-d8f51c5.json)
indexes exact settings, phase measurements, terminal receipt, accounting and
validation evidence without publishing payloads.

## Earlier diagnostics and executed result

The unchanged [92d8813 archive](compact-taskplan-92d8813.json) retains Collections'
HTTP 500, its separately authorized permanent closure and full
€0.4901093041855505198613703012 reservation. Fresh Review PR then stopped after
15 receipted calls and four corrections without an artifact or execution,
costing €0.5453390207055896205456322758. Its mixed-action responses exposed missing
exclusive-action guidance and unrestricted prose evidence requests. Revision
`d8f51c5` restored that instruction and exact host-issued navigation choices.
The earlier unsupported capability-absence claim does not establish a provider
navigation defect.

The [407243a archive](compact-taskplan-407243a.json) retains the fully receipted
17-call prerequisite-citation failure; `92d8813` corrected citation eligibility
without widening coverage or closure. The [bbd900d9 archive](compact-taskplan-bbd900d9.json)
retains four failed diagnostics and two separately frozen output profiles.
Its two reasoning-only exhaustions remain unexplained. Amazon's unchanged
extraction failure was not retried. Declared reasoning efforts remain `medium`,
`high` and `max`; unsupported `low` was not tested.

Source `efcb1d7aebc2de01154e946a749e66b83a5813ae` passed both real Collections
runtime variants after artifact-bound delegated test review: exact results,
order, duplicates and result-contributing parallel reusable calls passed, with
no safety violations, unrequested effects or runtime LLM. It used 42 calls,
three corrections, 81,886 input and 38,908 output tokens, 655.835 planning seconds
and €1.401111. Per-variant runtime timing, capacity beyond 100 items and extreme
numeric ranges remain unverified. The unchanged [efcb archive](compact-taskplan-efcb1d7a.json)
also retains the unexecuted Amazon and Review PR failures. That historical
success does not prove acceptance or repeatability of the current source.

## Campaign accounting

The post-Collections audit verifies **1,269 requests / 1,257 receipts**. All twelve
receiptless identities remain permanently closed; the journal separately counts
eleven unknown attempts. No reservation was released.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 38.813960721585355016440060418 |
| Unknown-usage reservations | 4.5812716608904292188749666752 |
| Spending/reservation upper bound | 43.395232382475784235315027093 |
| Available allowance | 196.634871450230520307129920207 |

Audit evidence: `e001b32762c7b72c71ba5e979c69f480ad2d6948cac9a80f56cd775abd1c559a`.
Lost historical exposure of €159.96989616729369545755505266 remains inside the
original €400 authorization. Including that exposure, cumulative spending and
reservations have an upper bound of approximately **€203.365129**. Exact decimal
strings are retained; independently summed costs can differ from ledger totals
at their final decimal places. Further dispatch must recheck balance and
readiness; these snapshot values grant no new allowance.

## Validation and remaining acceptance

The exact `d8f51c5` changes passed **2,497 Planning tests and 71 focused tests**
with warnings as errors. Its Planning package and Native AOT publish/smoke also
passed; all three logs and both artifact hashes/sizes were verified. These checks
cover Planning only. The unchanged `92d8813` archive separately records five
package builds, Native AOT planning, trimmed Server encrypted-storage smoke and
Git MCP publication/contract checks. Server publication excluded frontend and
bundled tools; existing documented warning policies remained unchanged.

The earlier full solution at `bbd900d9` completed with **7,496 passes, one stale
scripted-fixture failure and 13 skips** across 33 project results. Applying only
that fixture correction yielded **1,662 Server passes and one skip**. The d8
archive indexes both exact manifests, verified logs, fixture patch and TRX
hashes. Neither historical result is a final-revision full-solution pass.
Final-source full-solution and CI results remain outstanding.

Collections must pass again on the final candidate. Amazon requires complete
observations, product identities, workbook contents and browser closure. Review
PR requires pinned provenance, actual commands, local reports, cleanup and
independent assessment of the requested diff review. Artifact-bound review and
exact invocation confirmations remain mandatory. Only after all three
diagnostics pass may the 18 paired runs begin. Required checks must cover the
delivered revision; compilation and green tests are not business acceptance.

## Immutable evidence

The [d8f51c5 archive](compact-taskplan-d8f51c5.json) records the latest Judge
exhaustion. Earlier [92d8813](compact-taskplan-92d8813.json),
[407243a](compact-taskplan-407243a.json), [bbd900d9](compact-taskplan-bbd900d9.json),
[bb5b726](compact-taskplan-bb5b726.json), [efcb](compact-taskplan-efcb1d7a.json),
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
