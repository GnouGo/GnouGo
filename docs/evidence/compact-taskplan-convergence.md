# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. All three latest
diagnostics stopped without a complete artifact or runtime execution. An earlier
source passed both Collections runtime variants. The 18 alternating
baseline/candidate runs against `56942f5` remain unstarted.

## Latest frozen diagnostic

Source `2d0485d087191614ab05a927e3dcb15c2af96fb7`, cohort `compacta2d0485d0`,
retained pinned `gpt-5.5-2026-04-24`, medium reasoning, 64 calls and four
corrections. Input targets/ceilings stayed 3k/12k for navigation, 8k/16k for
drafting and 4k/8k for review, with output reservations of 16,384/16,384/4,096.
All three diagnostics stopped without checkpoints, complete artifacts or execution.

| Scenario | Attempts / receipts | Corrections | Planning seconds | Known cost, EUR |
| --- | ---: | ---: | ---: | ---: |
| Collections | 9 / 9 | 1 | 444.231 | 0.6811958946898705934850513164 |
| Amazon | 2 / 2 | 0 | 241.174 | 0.4699643016510486390004462293 |
| Review PR | 3 / 2 | 0 | 335.848 | 0.0497902721999107541276215975 |

Collections selected closure of its caller-input requirement before adding tasks.
The candidate correctly seeded the input, but the Judge received empty actual
imports and lacked declaration/scope navigation, then incorrectly reported the
input absent. Three evidence-only replan calls retrieved scope, input and original
source evidence. The one charged correction repeated the closure and stopped at
the progress guard. No finding was bypassed or automatically accepted.

Its second intent response was valid completed JSON with **15,163 tab characters**
(longest whitespace run 15,156). That call consumed 16,067 output tokens, including
only 365 reasoning tokens, took 303.6432291 seconds and cost
€0.4372735385988398036590807675. Original text occupied 16,828 bytes; serialized
parsed content occupied 1,690. Their separate hashes bind to the same receipt.
This formatting anomaly is distinct from empty output or reasoning exhaustion;
its cause remains unproved.

Amazon's second intent call exhausted **16,384 output tokens entirely on reasoning**
and returned no content (`MODEL_OUTPUT_LIMIT`), despite supplied original naming
and type context. It received 1,753 input tokens. No causal explanation is
established, and the unchanged failed request was not retried.

Review PR request 3 returned **HTTP 500 without a receipt or verified usage**.
Its full **€0.4922088353413654618473895582** reservation remains unknown usage.
Read-only permanent-retention inspection is saved, but authorization is pending
and no closure has been applied. Paid dispatch remains stopped; no uncertain
request or execution was replayed.

Known provider usage across the 13 receipted calls was **16,275 input / 42,143
output / 24,406 reasoning tokens**. Known cost is
**€1.2009504685408299866131191432**; including the new unknown reservation, the
cohort upper bound is **€1.6931593038821954484605087014**. Planning totalled
1,021.253 seconds. Completed call stages totalled 693.5599946 seconds; including
the failed HTTP attempt, observed call stages totalled 998.9564536 seconds.

| Complete-request measurement | p50 | p95 | Maximum |
| --- | ---: | ---: | ---: |
| All 14 constructed requests, bytes | 8,023 | 10,636 | 10,636 |
| Estimated input tokens | 2,930.5 | 3,802 | 3,802 |
| Collections requests, bytes | 8,187 | 10,293 | 10,293 |
| Amazon requests, bytes | 6,116.5 | 9,262 | 9,262 |
| Review PR requests, bytes | 3,129 | 10,636 | 10,636 |

All **98,659 constructed request bytes** reconcile with components and meet
captured ceilings. Median p50 and nearest-rank p95 are used; component estimates
are not billed usage. One completed output-limit response was empty. Four replan
calls comprise three evidence-only responses and one correction. Runtime latency
and calls per independently satisfied business obligation remain undefined.
The [2d0485d archive](compact-taskplan-2d0485d.json) retains phase measurements,
formatting and failure hashes, pending retention and safe evidence indexes.

The unchanged [5678374 archive](compact-taskplan-5678374.json) retains the previous
47 receipted calls and €1.6063364569388665774207942883 across three failed runs.
Its capacity-review and description-reread defects were corrected in `2d0485d`,
but these new diagnostics did not reach the phases needed to verify those fixes.
Subsequent declaration/closure context and response-format guidance corrections
have been integrated after this cohort; their live effect remains unverified.

## Earlier diagnostics and executed result

The unchanged [d8f51c5 archive](compact-taskplan-d8f51c5.json) retains 35
receipted Collections calls, two corrections and seven checkpoints, costing
€1.2391628898960277259397494006. Intermediate Judge call 35 exhausted 4,096 output
tokens entirely on reasoning, with no visible response or artifact. The verified
prompt/schema mismatch mixed draft authoring actions into review guidance;
`52ebb72` removes those actions while retaining factual task semantics and
historical requests. Causation of exhaustion remains unproved.

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

The post-Review-PR audit verifies **1,414 requests / 1,400 receipts**. Of fourteen
receiptless identities, thirteen are permanently closed; the new Review PR request
remains unclosed pending exact authorization. The journal separately counts
thirteen unknown transport attempts. Every reservation remains retained.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 45.069808851805813258662779906 |
| Unknown-usage reservations | 5.5635898004173452005837265346 |
| Spending/reservation upper bound | 50.633398652223158459246506441 |
| Available allowance | 189.396705180483146083198440859 |

Audit evidence: `e8556b4fdce8eb05347bac05e9bdfe4a77b2d6b5591ce96dab15f9256fa66a62`.
Pending retention inspection: `d03b13503b6ab602175c575e33c9049d10514939c1f5c1c174fe8df4f28425b4`.
Lost historical exposure of €159.96989616729369545755505266 remains inside the
original €400 authorization. Including that exposure, cumulative spending and
reservations have an upper bound of approximately **€210.603295**. Exact decimal
strings are retained; independently summed costs can differ from ledger totals
at their final decimal places. Further dispatch must recheck balance and
readiness; these snapshot values grant no new allowance.

## Validation and remaining acceptance

The subsequent caller-declaration and formatting changes passed 79 and 67
focused checks with warnings as errors. Their combined Planning run passed
2,533 of 2,534 tests; the single failure was a fixture comparing a 4,098-token
request to the 4k target instead of its captured 8k ceiling. The corrected
navigation/measurement fixture suite passed 65 tests, with 18 phase-policy tests
also passing. No production admission limit changed, and the initial failure
remains retained. Core and Planning Release packages, Native AOT publish/smoke
and the benchmark build passed. All 568 pinned MCP files were restored and
verified after the build. The combined validation manifest is
`f299c78544259fe1cfb5421c5cfc66500bddd0fb31ae7a059e883199f89c174d`;
safe logs, results and publication manifests are retained under
`closure-format-validation-20261023`. These changes have no live result yet.

The frozen capacity/discovery changes passed **199 focused checks** and the
benchmark build with warnings as errors. The retained full Planning run records
**2,526 passes and two stale fixture failures**. Its test-only compatibility
correction then passed **eight focused cases**; this is not a second full-suite
pass. The initial benchmark build's `IL2026`/`IL3050` failure remains retained;
explicit JSON node overloads resolved it. Planning Release pack, Native AOT
publish and native smoke passed afterward. Publication used the `56783741` base
plus the captured production patch; the combined manifest verified 437 component
files byte-matched the committed `2d0485d` source. No clean-base publication or
live business success is inferred. Later correction validation remains separate
from this frozen cohort.

Historical focused suites, packages and publication evidence remain indexed in
their immutable revision archives. The `61d9f0f` source passed 2,520 Planning
tests; later instruction changes received affected focused checks. Earlier
passing CI on [52ebb72](https://github.com/GnouGo/GnouGo/actions/runs/39855405218)
and [0b44557](https://github.com/GnouGo/GnouGo/actions/runs/39857068574) remains
revision-scoped.

The first local full-solution attempt at `0b44557` returned exit 1 after the
pinned Copilot CLI download encountered EOF (`MSB3923`), leaving Server tests
unexecuted. The completed project results show **5,858 passes and 12 skips**,
with no failed test among those executed. Its manifest retains the build error
and a generated timestamp-comment change, restored to exact HEAD before the
separate rerun. The exact clean `0b44557` rerun completed with warnings as errors:
**7,529 passed, zero failed and 13 skipped across 33 projects**, in 5,185.065
seconds. The [5678374 archive](compact-taskplan-5678374.json) indexes its terminal
manifest separately from that revision's publication evidence. This historical full-solution success does not
establish a full-solution pass for the current source; the original download
failure remains retained.

The unchanged `92d8813` archive records five package builds, Native AOT planning,
trimmed Server encrypted-storage smoke and Git MCP publication checks. The d8
archive separately retains the earlier `bbd900d9` full-solution fixture failure
and corrected Server run. Their historical scopes remain unchanged; further
source changes require corresponding final validation.

Collections must pass again on the final candidate. Amazon requires complete
observations, product identities, workbook contents and browser closure. Review
PR requires pinned provenance, actual commands, local reports, cleanup and
independent assessment of the requested diff review. Artifact-bound review and
exact invocation confirmations remain mandatory. Only after all three
diagnostics pass may the 18 paired runs begin. Required checks must cover the
delivered revision; compilation and green tests are not business acceptance.

## Immutable evidence

The [2d0485d archive](compact-taskplan-2d0485d.json) records the three failed
diagnostics, response-format anomaly, reasoning exhaustion, unresolved HTTP 500,
current campaign snapshot and frozen validation evidence. The unchanged
[5678374 archive](compact-taskplan-5678374.json) records its three fully receipted
failures, undispatched prospective envelope and historical campaign snapshot.
The unchanged
[875022e archive](compact-taskplan-875022e.json) records the preceding Collections
open-scope stop and its historical campaign balance. The unchanged
[61d9f0f archive](compact-taskplan-61d9f0f.json) records extraction clarification.
The [2e5b3a0 archive](compact-taskplan-2e5b3a0.json) records the preceding fully
receipted Review PR stop and exact-revision validation.
The [0b44557 archive](compact-taskplan-0b44557.json) records Review PR’s
fully receipted discovery stop and the separately authorized Collections closure.
The unchanged [52ebb72 archive](compact-taskplan-52ebb72.json) preserves the
earlier uncertain transport attempt and unapplied inspection snapshot. The
[d8f51c5 archive](compact-taskplan-d8f51c5.json) retains the earlier Judge
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
