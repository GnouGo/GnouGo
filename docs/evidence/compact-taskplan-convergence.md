# Compact TaskPlan convergence

Collections has passed one complete live diagnostic on both runtime variants.
Overall business acceptance remains incomplete and PR #120 stays Draft. Amazon
and Review PR stopped before execution. The 18 alternating baseline/candidate
comparison runs against `56942f5` have not started; one diagnostic success does
not establish repeatability or comparative improvement.

## Latest frozen diagnostics

Source `efcb1d7aebc2de01154e946a749e66b83a5813ae`, cohort `compactaefcb1d7a`, used the
pinned `gpt-5.5-2026-04-24` with medium reasoning in every phase. Navigation had a
3k target / 12k ceiling and 16,384 output tokens; drafting 8k / 16k and 8,192 output;
Judges 4k / 8k and 4,096 output. The 64-call, correction, graph, cumulative and
financial limits stayed fixed. The [safe aggregate](compact-taskplan-efcb1d7a.json)
contains source/settings/MCP/oracle hashes, reconciled components and failure evidence.

| Scenario | Calls / receipts / corrections | Input / output / reasoning tokens | Known cost | Planning duration | Business outcome |
| --- | ---: | ---: | ---: | ---: | --- |
| Amazon | 9 / 9 / 0 | 15,571 / 5,649 / 4,474 | €0.219786 | 110.328 s | Repeated evidence stop; no candidate or execution |
| Collections | 42 / 42 / 3 | 81,886 / 38,908 / 32,538 | €1.401111 | 655.835 s | Both nominal and alternate real runtime variants passed |
| Review PR | 4 / 4 / 0 | 2,548 / 18,986 / 18,219 | €0.517480 | 288.871 s | Interface extraction exhausted output; no candidate or execution |

The 55 receipts cost **€2.1383764329512130098640362567**, with no new uncertain
attempt. All 536,202 complete-request bytes reconcile and every request stayed
within its captured ceiling. Provider reasoning is included in output tokens.
Completed-call latency was 100.817 / 631.639 / 284.199 seconds respectively,
excluding orchestration. Component estimates are not billed usage.

Collections' artifact `009e950e9adff7bfd270f0606cdee623a09740dda6f0107df0d4b5972b1a0a8f`
passed independent structural review and artifact-bound `delegated_test` review
at revision 42 before approval at revision 43. Both runtime variants matched exact
results, order and duplicates; result-contributing reusable parallel calls were
checked. Effects and safety violations were empty, with no runtime LLM dependency.
The approval/runtime command took 2.302096 seconds; per-variant runtime duration
was not measured in this frozen binary. Finite capacity 100 and extreme numerical
ranges remain unverified. Runtime success does not remove those limits.

Amazon's initial four operation descriptors and their exact description addresses
disappeared after later directory pages were fetched. Original contracts remained
stored. The model regrouped two supplied pages; the existing individual-evidence
progress guard correctly stopped it. A generic bounded navigation correction is
under validation. The evidence proves lost navigation, not why the model repeated
its request; the fix has no live proof yet.

Review PR's fourth request was 3,731 bytes / 1,500 estimated input tokens. Its
explicit output-limit receipt records 635 input and 16,384 output tokens, entirely
reasoning, with no visible response. All four calls have receipts. The current
schema simplification did not eliminate this failure, and its cause remains
unproved. No Judge or execution was reached. No unchanged retry, uncertain replay
or mid-session escalation occurred.

| Phase | Requests | Complete bytes p50 / p95 / max | Estimated tokens p50 / p95 / max |
| --- | ---: | ---: | ---: |
| Intent | 8 | 3,430 / 5,113 / 5,113 | 1,399.5 / 1,961 / 1,961 |
| Selection | 13 | 8,631 / 9,884 / 9,884 | 3,133 / 3,551 / 3,551 |
| Discovery | 6 | 9,196.5 / 11,254 / 11,254 | 3,322 / 4,008 / 4,008 |
| Generation | 10 | 13,211 / 14,377 / 14,377 | 4,660 / 5,049 / 5,049 |
| Local Judge | 13 | 9,174 / 21,216 / 21,216 | 3,314 / 7,328 / 7,328 |
| Intermediate Judge | 4 | 12,598.5 / 21,405 / 21,405 | 4,456 / 7,391 / 7,391 |
| Correction | 1 | 15,477 / 15,477 / 15,477 | 5,415 / 5,415 / 5,415 |

Percentiles use median p50 and nearest-rank p95. Correction accounting includes
reselection attempts, so charged corrections need not equal correction-phase calls.
All phase costs, provider tokens and component totals are in the aggregate.

## Campaign accounting and retained uncertainty

The new encrypted audit verifies 1,154 requests / 1,144 receipts. All ten
receiptless identities retain permanent closures; the journal separately counts
nine attempts with unknown usage. No reservation was released.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 34.018190704700968630587398908 |
| Unknown-usage reservations | 3.6010530525193281791522260728 |
| Spending/reservation upper bound | 37.619243757220296809739624981 |
| Available allowance | 202.410860075486007732705322319 |

Audit evidence: `daed86512c2b6fdedb3094ce1347c6992ee80908bfc49fcee37688d0ce4f2434`.
Separate lost historical exposure of €159.96989616729369545755505266 remains within
the original €400 authorization. No ceiling increased. Existing closures preserve
all original evidence and forbid replay and continuation of their abandoned runs.
Further paid dispatch must recheck available funds and provider readiness.

## Validation and remaining acceptance

The frozen efcb source passed 623 Compact tests with warnings as errors; the
interrupted-process operator previously passed 495 focused retention/campaign tests.
Current source changes require their own validation and a fresh source/settings/
oracle freeze before live dispatch. New reporting measures distinct frozen business
obligations covered by independent artifact review, separately from actual runtime
acceptance; it never uses task, requirement or checkpoint counts as useful-work
credit. It does not retroactively recalculate missing historical measurements.

The [29ffd CI](https://github.com/GnouGo/GnouGo/actions/runs/39811045971) passed at
18:31:53 UTC on October 22: 6,378 selected-suite tests, one additional published
Git test, five Flow packages, benchmark build and Linux Native AOT, with one browser
opt-in skip and no compiler warnings. Its PR merge checkout was `7428e377`.
Final log SHA-256: `8d770583619c95be81236455d74557e6e1e1ee791456556615fb3451ccef36a2`.
The later d488/efcb runs were still pending at the 18:40 UTC snapshot. Selected CI
projects do not establish current-source, complete local solution or published-storage
validation, and no pending run is reported as successful.

Amazon still requires complete observations, independently checked workbook contents
and browser closure. Review PR requires pinned provenance, actual checks, local
reports and cleanup. Collections must pass again on the final frozen candidate.
Artifact-bound review and exact individual confirmations remain mandatory. Only
after all diagnostics pass may the 18 alternating measured runs begin. Final
full-solution warnings-as-errors, independent packages, AOT and published encrypted
storage checks remain required. No new execution engine or final whole-plan Judge
is introduced.

## Immutable evidence

The [efcb aggregate](compact-taskplan-efcb1d7a.json) indexes the current safe metrics
and execution evidence. The prior [d488 aggregate](compact-taskplan-d488720a.json)
retains 53 receipts, €2.1888740780236381409401937261 cost and three pre-execution
failures, including reasoning exhaustion, interface omissions and premature cleanup
review. It is unchanged. Prior [29ffd](compact-taskplan-29ffd813.json),
[3adc](compact-taskplan-3adc335.json), [4a43](compact-taskplan-4a43ea1.json),
[8952](compact-taskplan-8952a0a.json), [743](compact-taskplan-743e4a3.json),
[410](compact-taskplan-41077d0.json), [663](compact-taskplan-663cf72.json),
[1984](compact-taskplan-1984bdc.json), [afc](compact-taskplan-afc342c.json),
[5c](compact-taskplan-5c01f98.json), [08ad](compact-taskplan-08ad5db.json) and
[cd174ea](compact-taskplan-cd174ea.json) archives retain their original failures,
accounting and validation limitations. [October 19](compact-taskplan-live-2026-10-19.md),
[October 20](compact-taskplan-live-2026-10-20.md) and
[historical components](compact-taskplan-request-components-2026-10-20.json) remain
unchanged. Original payloads stay encrypted; reports contain safe metadata and grant
no execution authority.
