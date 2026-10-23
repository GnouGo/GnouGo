# Compact TaskPlan convergence

Business acceptance remains incomplete and PR #120 stays Draft. All three latest
diagnostics stopped without a complete artifact or runtime execution. An earlier
source passed both Collections runtime variants. The 18 alternating
baseline/candidate runs against `56942f5` remain unstarted.

## Latest frozen diagnostic

Source `56783741dd52295cb505f66300c30b8cff95d340`, cohort `compacta56783741`,
retained pinned `gpt-5.5-2026-04-24`, medium reasoning, 64 calls and four
corrections. Input targets/ceilings stayed 3k/12k for navigation, 8k/16k for
drafting and 4k/8k for review, with output reservations of 16,384/16,384/4,096.

| Scenario | Receipted calls | Corrections | Checkpoints | Planning seconds | Cost, EUR |
| --- | ---: | ---: | ---: | ---: | ---: |
| Collections | 16 | 1 | 3 | 243.268 | 0.4557072735385988398036590807 |
| Amazon | 14 | 1 | 0 | 221.427 | 0.5227621597501115573404730031 |
| Review PR | 17 | 1 | 1 | 279.545 | 0.6278670236501561802766622045 |

Collections progressed past the earlier container-opening error. A Judge then
incorrectly treated an unspecified loop capacity as a business restriction;
correction ended in clarification. The accepted checkpoints do not establish
the complete workflow's correctness or capacity.

Amazon overlooked a partial original-description continuation, then batched
description rereads while the captured guard permits qualified rereads only as
individual actions. Repeated non-progress stopped discovery before drafting.
Review PR reached the same mismatch when its last call combined two new
operation descriptions with a qualified reread. Both stopped with
`REPLAN_NO_PROGRESS`. The next candidate distinguishes unspecified loop capacity
from an explicit required item count and constrains discovery to unseen evidence
batches or an eligible single-description reread. Drafting and discovery share
the same validated original prerequisite citations. These focused corrections
preserve historical requests, runtime limits and correction authority; their live
effect remains unverified.

Review PR completed intent extraction after one local correction: it repeated
accepted interfaces without their old dispositions, then resolved the
`SOURCE_COVERAGE` feedback without injected answers. A `git_clone` fragment
passed local review and reached one checkpoint; no clone was executed.

Across the three runs, **47 calls have receipts**, with **114,736 input / 40,874
output / 28,848 reasoning tokens**, costing
**€1.6063364569388665774207942883**. Planning totalled 744.240 seconds; completed
call stages totalled 686.707841 seconds. No empty response, admission rejection
or new uncertainty occurred. Review PR also retained one prospective draft
envelope superseded before dispatch, without an HTTP attempt, reservation,
failure or receipt. It is counted among constructed requests, not paid calls.

| Complete-request measurement | p50 | p95 | Maximum |
| --- | ---: | ---: | ---: |
| All 48 constructed requests, bytes | 11,340 | 21,331 | 21,644 |
| Estimated input tokens | 4,036.5 | 7,367 | 7,471 |
| Collections requests, bytes | 8,885 | 14,875 | 14,875 |
| Amazon requests, bytes | 9,779 | 21,511 | 21,511 |
| Review PR requests, bytes | 12,772.5 | 21,644 | 21,644 |

All **554,690 constructed request bytes** reconcile with components and meet
captured ceilings. Median p50 and nearest-rank p95 are used. Reasoning is included
in output; component estimates are not billed usage. Runtime latency and calls
per independently satisfied business obligation remain undefined. The
[5678374 archive](compact-taskplan-5678374.json) records per-phase components,
provider usage, terminal evidence hashes and the safe-source index without
duplicating every call record.

The unchanged [875022e archive](compact-taskplan-875022e.json) retains the earlier
20-call Collections attempt that exported an unfinished loop, costing
€0.6640919232485497545738509593. Its generic opening/child/closure clarification
is present in this cohort; the latest failure is a different scoped-review issue.

The [61d9f0f archive](compact-taskplan-61d9f0f.json) retains the preceding four-call
Review PR extraction clarification, costing €0.2414547077197679607318161535.
The exact request contained both original pages, but the model treated required
local report contents as a new public return interface. `875022e` clarified the
generic distinction between artifact obligations and returned values without
injecting evaluation-time answers or changing schemas. The latest Review PR
diagnostic completed extraction, but did not complete its workflow.

The preceding [2e5b3a0 archive](compact-taskplan-2e5b3a0.json) retains 33 receipted
calls, four corrections, one failure-feedback checkpoint and no complete artifact
or execution. All requests met their limits, with a 27,501-byte / 9,423-token
estimated peak. Its €1.310428 cost and 546.799 planning seconds exceeded the
prior 25-call discovery failure's €1.219634 and 436.940 seconds. Smaller requests
did not establish business success. Exact inspection demonstrated incomplete
provider visibility and a Judge that overlooked a preserved public root export;
`61d9f0f` corrected those presentation defects with no model or permission change.

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

The post-Review-PR audit verifies **1,400 requests / 1,387 receipts**. All thirteen
receiptless identities are permanently closed with their reservations retained.
The journal separately counts twelve unknown attempts. No reservation was released.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 43.868858383264983272049660762 |
| Unknown-usage reservations | 5.0713809650759797387363369764 |
| Spending/reservation upper bound | 48.940239348340963010785997738 |
| Available allowance | 191.089864484365341531658949562 |

Audit evidence: `e1d4ff3a9ffac6150988cf2910270e8dfb48bd4ccdd5db7d04375d0384535454`.
Lost historical exposure of €159.96989616729369545755505266 remains inside the
original €400 authorization. Including that exposure, cumulative spending and
reservations have an upper bound of approximately **€208.910136**. Exact decimal
strings are retained; independently summed costs can differ from ledger totals
at their final decimal places. Further dispatch must recheck balance and
readiness; these snapshot values grant no new allowance.

## Validation and remaining acceptance

The capacity/discovery candidate passed 199 focused checks and the benchmark
build with warnings as errors. Its combined Planning run passed 2,526 tests and
found two stale assertions expecting repeated-evidence rejection after schema
validation. The corrected compatibility fixture passed all eight focused cases,
including exact issued-request recovery; no production change was needed. The
initial AOT analyzer failure selected a generic JSON overload; explicit node
overloads resolved it. Combined production packaging and Native AOT smoke pass.
Both initial failures remain retained. These checks do not establish live success.

The `5678374` container-opening clarification passed **44 focused opening and
reselection tests** with warnings as errors, plus the exact benchmark build.
Existing fixtures now exercise legal empty openings, retained open requirements,
unchanged accepted plans and rejected consumption of unfinished results across
all five container kinds. Independent review found no remaining authority or
compatibility issue. Its exact Planning package and Native AOT publish/smoke
passed with warnings as errors. Its [CI](https://github.com/GnouGo/GnouGo/actions/runs/39897691304)
is still running at this snapshot. These checks are separate from the three live
failures above.

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
seconds. The new archive indexes its terminal manifest separately from the exact
`5678374` publication evidence. This historical full-solution success does not
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

The [5678374 archive](compact-taskplan-5678374.json) records all three current
diagnostics, their fully receipted calls, the undispatched prospective envelope,
current campaign snapshot and revision-specific validation. The unchanged
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
