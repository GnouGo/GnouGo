# Compact TaskPlan convergence

**Business acceptance remains incomplete; PR #120 stays Draft.** The latest completed
Collections, Amazon and Review PR diagnostics use `7697449ce24fb54804cb31afc33a217c9105c65f`.
None produced a complete artifact or executed workflow. A fresh Collections experiment
with only the review output allowance raised to 8,192 also stopped without an artifact.
Amazon's separately frozen 9k-input experiment also stopped without a checkpoint.
A narrowly reviewed instruction correction is pending a fresh Collections diagnostic; no live benefit is claimed.
The 18 alternating runs
against `56942f5` remain gated on all three candidate executions passing independent oracles.

## Current behavior and technical evidence

The incremental TaskPlan retains original requirements, deterministic validation,
local/intermediate Judges, checkpoints and JavaScript → YAML v15 compilation. Current-step
discovery, bounded owner/origin facts and exact original contracts preserve scope and
execution authority. Ordinary same-scope results need no exports; boundary crossings do.
Open containers remain unavailable. Inconclusive reports and admission failures do not
create correction or execution authority; no final whole-workflow Judge was added.

Fresh transport permits at most three synchronous attempts after complete durably saved
transient responses. Every physical attempt requires financial, token, call, time and
mandatory-review admission; failed unknown usage remains reserved after successful retry.
Saved success recovers before new-dispatch admission. Historical bytes/policies remain
unchanged. Planning captures five hours separately from runtime limits. Oversized evidence
batches can narrow to a singleton without charging host-only admission as a repair;
indivisible oversized requests stop. Existing business findings retain correction limits.

| Exact source or validated change | Technical result, with warnings as errors |
| --- | --- |
| `7697449c` | **100 focused / 2,721 full Planning passed**, no failures/skips/compiler diagnostics; committed files match the tested repair patch; **12/12 publication stages passed**. **7,846 full-solution passed / 0 failed / 13 skipped**,33 projects,4,490 unchanged source files. [Planner CI](https://github.com/GnouGo/GnouGo/actions/runs/40038029326) remains pending at the indexed snapshot |
| `a9348669` | **7,839 full-solution passed / 0 failed / 13 skipped**, 33 projects; 117 focused/2,714 Planning and 12 publication stages passed. This historical source does not contain repair version 12 |
| `975fd04f` | **7,834 full-solution passed / 1 stale assertion failed / 13 skipped**, 33 projects, no compiler diagnostics. The single-diagnostic assertion was corrected in `a9348669`; 81 historical-fixture focused tests passed with one intentional browser skip |
| `504a557d`, `7e19f7f8` | Each 12 publication stages passed. `504a557d` full solution: 7,811 passed/23 failed/13 skipped; 22 historical-fixture failures and one stale assertion, corrected later |
| `e0959e3` | 12 publication stages passed; full solution 7,796 passed/22 historical-fixture failures/13 skipped; original failures retained |
| `d0413d2` | 7,690 full-solution passed/0 failed/13 skipped; 237 focused/2,661 Planning; 11 publication stages; [CI passed](https://github.com/GnouGo/GnouGo/actions/runs/40002249895) |
| `f3cb064` | 7,685 full-solution passed/0 failed/13 skipped; 92 focused/2,656 Planning; 11 publication stages; [CI passed](https://github.com/GnouGo/GnouGo/actions/runs/39994400563) |
| `35022fa`, `131801d4` | Historical full-solution passes 7,674 and 7,661, each 0 failed/13 skipped; `35022fa` also 11 publication stages and [CI passed](https://github.com/GnouGo/GnouGo/actions/runs/39968367834) |

The [current technical index](compact-taskplan-7697449-technical-validation.json) binds exact
manifests, source hashes and dated pending observations;769 and a934 planner CI were still active at 00:55 UTC. Publication covers six independent
packages, Planning AOT revisions 2/3/4, published encrypted storage and Git AOT/contracts.
Current [Proxy AOT](https://github.com/GnouGo/GnouGo/actions/runs/40038029318) and
[standalone](https://github.com/GnouGo/GnouGo/actions/runs/40038029330) CI passed.
Historical a934 [standalone rerun](https://github.com/GnouGo/GnouGo/actions/runs/40033155614/attempts/2)
and [Proxy AOT](https://github.com/GnouGo/GnouGo/actions/runs/40033155627) passed; its first
artifact-service ETIMEDOUT and [planner CI record](https://github.com/GnouGo/GnouGo/actions/runs/40033155679)
remain separate evidence. Pending statuses require terminal verification before later delivery.

Earlier focused evidence remains in the [confirmed-retry](compact-taskplan-confirmed-retries-validation.json),
[evidence-recovery](compact-taskplan-evidence-recovery-validation.json),
[same-scope](compact-taskplan-same-scope-result-validation.json) and
[repair](compact-taskplan-invalid-retained-repair-validation.json) indices; filters overlap.
Fresh repair version 12 excludes JSON(original) only for an exact required TASK_LITERAL_INVALID
at that binding. Valid conversions, ordinary multiline replacements and issued 10/11 requests
retain their original authority and recovery. Technical passes do not establish live convergence.

## Latest completed live diagnostics

| Frozen attempt | Logical calls / physical attempts / receipts | Corrections / checkpoints | Planning seconds | Known cost EUR | Terminal result; none executed |
| --- | ---: | ---: | ---: | ---: | --- |
| [7697449 Amazon review9](compact-taskplan-7697449-amazon-review9.json) | 31/31/31 | 3/0 | 810.637 | 1.5096340919232485497545738509 | Judge 31 omitted original-obligation grounding; report rejected |
| [7697449 Collections review8](compact-taskplan-7697449-collections-review8.json) | 47/47/47 | 4/8 | 1,075.530 | 1.8781838464970995091477019188 | Grounded redundant final alias; no empty Judge response |
| [7697449 Collections](compact-taskplan-7697449-collections.json) | 17/18/17 | 1/3 | 714.674 | 0.6770771976796073181615350287 | Intermediate Judge 17 exhausted 4,096 reasoning tokens, zero text |
| [7697449 Amazon](compact-taskplan-7697449-amazon.json) | 40/40/40 | 2/4 | 1,000.806 | 2.0205310129406514948683623382 | Mandatory Judge 41 denied before dispatch: 8,555 > 8,000 input tokens |
| [7697449 Review PR](compact-taskplan-7697449-code.json) | 25/25/25 | 3/1 | 856.946 | 1.6618652387327086122266845156 | Grounded missing caller review text persists after three repairs |
| [a9348669 Review PR](compact-taskplan-a934866-code.json) | 51/52/50 | 2/5 | 1,657.740 | 2.7922579205711735832217759928 | Confirmed HTTP 400: repair schema retained an already invalid literal |
| [975fd04f Review PR](compact-taskplan-975fd04-code.json) | 39/39/39 | 4/3 | 959.263 | 1.9388041053101294065149486836 | False same-scope availability premise, export repair, then zero-action Judge |
| [504a557d Amazon](compact-taskplan-504a557-amazon.json) | 13/13/13 | 0/1 | 417.994 | 0.8039446675591253904506916556 | Judge omitted original-obligation grounding; handle allegation unproven |

Collections' local Judge 16 returned no findings; intermediate 17 consumed its retained
high reasoning 4,096 output allowance. Its complete request was 9,379 bytes/3,383 estimated input
tokens, below 8k. A closed multiplication group exists, but the parallel loop opening remains
tentative: no ordered duplicate-preserving reusable calls or public result were executed.
The [phase supplement](compact-taskplan-7697449-collections-phase-corrections.json) attributes
one genuine correction to an invalid group-input binding. HTTP 500→200 recovered during
extraction, retaining **EUR 0.9308701472556894243641231593**; no new receiptless request exists.

Collections review8 closed its reusable multiplication group and parallel loop, exported the
root result and closed both model requirements; the root boundary still remained open.
Twenty Judge receipts were nonempty; two used 6,437 and 4,583 output tokens, above the old
4,096 allowance. This different generated trajectory does not prove a matched-request cure.
[Judge 38](compact-taskplan-7697449-collections-review8-judge38.json) incorrectly attributed
new output work to an empty root-closure candidate; its selected objective nevertheless
remained mismatched to the caller-declaration requirement. Final selection legally offered
root closure, but the model chose another value alias. Judge 47 correctly identified that
actual redundancy after four corrections. Closure authority was present; neither static
structure nor model requirement closure proves execution. Separately, the
[reviewed wording patch](compact-taskplan-scope-basis-wording-validation.json) corrects an instruction overclaim: `scopeInterface` includes candidate additions
only with `basis:tentative_candidate`; `draft.outputs` lists additions. Its 90 existing focused
tests passed WAE, but the patch is outside the completed `7697449` full/live source and has no
demonstrated effect on Judge38 or convergence. Schemas, grounding and authority are unchanged.

Amazon's 45 constructed requests comprise 40 receipts, 4 prepared and one denied review.
Judge 41's 24,895 bytes include 10,286 bytes of the necessary whole observation interface;
no obsolete catalog or safe normative pruning was demonstrated. A separately frozen 9k
ceiling could admit it, without proving semantics or convergence. Sixteen discovery calls
include repeated original browser pages across steps. The first executable producer was
accepted after call 18; no requirement closed. Repair 17 changed wording after a handle/output
claim mismatch; handle necessity and avoided correction remain unproven. The other repair
selected a missing URL producer. Full phase/formatting data stays in the immutable archive.

Amazon review9 repeatedly selected a close step for a new session-producing objective.
Reselections 8/14 and repair 21 account for its three corrections; 20 mixed an extension with
reselection and was correctly rejected. Sole Judge 31 cited a concrete task plus step/operation,
but no offered original requirement. This unsupported report correctly stopped without a
checkpoint or reassessment; it was not an explicit inconclusive response. Its partial operation
description limits assessment of the handle claim. All requests fit their ceilings; Judge31
needed only 3,854 estimated input tokens, so the 9k allowance provided no demonstrated benefit.

Review PR accepted a clone with the exact repository URL, pinned head and fixed directory.
Its next action omitted `input(reviewText)` despite a typed available declaration and legal
JSON object/array binding grammar in every author request. Repairs 20/22/23 did not supply it;
22 also duplicated the `task` input and was correctly rejected. Judge 24 retrieved the exact
original declaration, then 25 grounded the omission. No same-scope/export or premature-closure
finding recurred. All Judges were at most 6,820 input tokens; the 9k ceiling's benefit is unproven.
The frozen toolchain PATH was verified, but no checks, report or cleanup executed. Optional
provider latency checkpoints are not independently measured HTTP durations. None of these
runs demonstrates avoided corrections or causal convergence improvement.

The a934 archive separately retains 500→200 and confirmed schema 400 reservations totaling
EUR 1.5331593038821954484605087015; the existing rejection operator created no receipt/refund.
Its clone still uses a PR-page URL and lacks required pinned provenance; unchanged execution
guards would reject it. Six earlier Judges without findings do not establish an avoided repair.
Prior [7e19 Review PR](compact-taskplan-7e19f7f-code.json), [Amazon](compact-taskplan-7e19f7f-amazon.json)
and [formatting](compact-taskplan-7e19f7f-response-format.json), [e095 Collections](compact-taskplan-e0959e3-collections.json),
[1b Amazon](compact-taskplan-1b749a1-amazon.json), [d041 Review PR](compact-taskplan-d0413d2-code.json)
and [Amazon](compact-taskplan-d0413d2-amazon.json) retain their distinct failures; the latter
reached the former host 30-minute limit.
Earlier controls remain [f3 Amazon](compact-taskplan-f3cb064-amazon.json),
[Review PR](compact-taskplan-f3cb064-code.json) and [high-navigation Amazon](compact-taskplan-f3cb064-amazon-high.json).
Source/settings differences and unmatched runs cannot establish architectural causality.

## Audited allowance after the Amazon 9k experiment

The post-Amazon review9 snapshot has **22 receiptless identities, all permanently closed**,
no pending abandonment and **24 unknown physical-attempt records**. Successful logical
requests can retain failed-attempt reservations; those counts are distinct.

| Ledger measurement | EUR |
| --- | ---: |
| Campaign ceiling | 240.0301038327063045424449473 |
| Known spending | 93.38730104279198014844412749 |
| Retained unknown-usage reservations | 13.336981143567724495541334746 |
| Spending/reservation upper bound | 106.72428218635970464398546224 |
| Available before the pending Collections wording diagnostic | 133.30582164634659989845948506 |

The [Amazon review9 archive](compact-taskplan-7697449-amazon-review9.json) binds that audit. With prior lost
historical exposure EUR 159.96989616729369545755505266, cumulative spending/reservations
are **EUR 266.69417835365340010154051490** within the original EUR 400 authorization.
Original CLR decimal precision differences remain disclosed. **This is not the balance
after any later diagnostic dispatches.** No later spending is inferred from this snapshot.

## Acceptance and chronological immutable evidence

Earlier `efcb1d7a` passed both Collections runtime variants. Current-source Collections,
Amazon and Review PR must execute and pass frozen independent oracles before paired runs.
Artifact review and exact individual confirmations remain mandatory. Current useful-business
obligation counts are zero; calls per satisfied obligation are undefined and runtime latency
unavailable. Compilation, checkpoints and technical passes are not business acceptance.
No avoided correction, causal convergence improvement or eventual success is claimed.
Archives below are grouped newest to oldest; their failures and validation scope are immutable.

- Prior diagnostics: [35022fa Collections](compact-taskplan-35022fa-collections-pending.json) and [authorized closure](compact-taskplan-35022fa-collections-retention.json); [2d6cbce Amazon](compact-taskplan-2d6cbce-amazon.json) and [Review PR](compact-taskplan-2d6cbce-code.json). Their failures and validation scope remain immutable.
- Frozen `131801d4`: [Collections control](compact-taskplan-131801d4-collections.json), [Amazon](compact-taskplan-131801d4-amazon.json), [Review PR](compact-taskplan-131801d4-code.json), [Collections output32](compact-taskplan-131801d4-collections-output32.json), [Collections high Judge](compact-taskplan-131801d4-collections-high-pending.json) and [its authorized abandonment](compact-taskplan-131801d4-collections-high-retention.json). All five failed to reach artifact execution; output-limit, correction and transport evidence remains separate. No causal reasoning-profile benefit or avoided corrections is proven.
- Prior producer-proof failure: [b1c29b359 Collections](compact-taskplan-b1c29b359.json).
- [615dd0e96 Review PR](compact-taskplan-615dd0e96.json) and [exact authorized closure](compact-taskplan-615dd0e96-retention.json), retained separately.
- Previous: [f05d07c2a Amazon](compact-taskplan-f05d07c2a.json), [16d8e83e3 Collections](compact-taskplan-16d8e83e3.json).
- Earlier: [1a cohort a](compact-taskplan-1a236234.json), [b](compact-taskplan-1a236234-b.json), [c](compact-taskplan-1a236234-c.json).
- Earlier: [9d23d5b](compact-taskplan-9d23d5b.json); prior [ae1d50aa](compact-taskplan-ae1d50aa.json), [365c8326](compact-taskplan-365c8326.json), [9246376](compact-taskplan-9246376.json), [2d0485d](compact-taskplan-2d0485d.json); [offline extraction experiments](compact-interface-ce9b610.json) made no paid calls or causal claim.
- [5678374](compact-taskplan-5678374.json), [875022e](compact-taskplan-875022e.json), [61d9f0f](compact-taskplan-61d9f0f.json), [2e5b3a0](compact-taskplan-2e5b3a0.json), [0b44557](compact-taskplan-0b44557.json), [52ebb72](compact-taskplan-52ebb72.json), [d8f51c5](compact-taskplan-d8f51c5.json).
- [92d8813](compact-taskplan-92d8813.json), [407243a](compact-taskplan-407243a.json), [bbd900d9](compact-taskplan-bbd900d9.json), [bb5b726](compact-taskplan-bb5b726.json), [efcb](compact-taskplan-efcb1d7a.json), [d488](compact-taskplan-d488720a.json), [29ffd](compact-taskplan-29ffd813.json).
- [3adc](compact-taskplan-3adc335.json), [4a43](compact-taskplan-4a43ea1.json), [8952](compact-taskplan-8952a0a.json), [743](compact-taskplan-743e4a3.json), [410](compact-taskplan-41077d0.json), [663](compact-taskplan-663cf72.json), [1984](compact-taskplan-1984bdc.json), [afc](compact-taskplan-afc342c.json), [5c](compact-taskplan-5c01f98.json), [08ad](compact-taskplan-08ad5db.json), [cd174ea](compact-taskplan-cd174ea.json).
- [October 19](compact-taskplan-live-2026-10-19.md), [October 20](compact-taskplan-live-2026-10-20.md), [historical components](compact-taskplan-request-components-2026-10-20.json); historical passing CI [52ebb72](https://github.com/GnouGo/GnouGo/actions/runs/39855405218), [0b44557](https://github.com/GnouGo/GnouGo/actions/runs/39857068574).

Archives retain failures, uncertainty and exact validation scope. Original payloads remain
encrypted; safe reports grant no execution authority. Finalize pending CI and later-source validation against their terminal evidence before
publishing a later summary.
