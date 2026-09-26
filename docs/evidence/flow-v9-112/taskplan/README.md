# TaskPlan candidate: completed, acceptance failed

Frozen source: `78b8b3468104e576358839c5315846957746c519`. One live cohort completed all nine authorized outcomes. **5/9 were correct.** Coverage, model/limits and bounded usage are comparable; correctness regressed against the best retained baselines. No unsafe or incorrectly approved execution was observed. Implementation and paid evaluation stopped after this cohort. PR #113 remains draft.

| Case | Parent | Previous candidate | Stabilization | TaskPlan |
| --- | ---: | ---: | ---: | ---: |
| conditional | 3/3 | 3/3 | 2/3 | **3/3** |
| review_french | 0/3 | 2/3 | 3/3 | **1/3** |
| review_distractors | 0/3 | 2/3 | 1/3 | **1/3** |

The five approved artifacts passed their independent execution variants. The four unsuccessful plans stopped before approval; these failures remain in the denominator. Conditional planning recovered the stabilization regression, but that does not offset the review-case regressions. Three repetitions are a comparison sample, not a broad reliability guarantee.

## Retained failures

- `review_french`, repetitions 1 and 2: the model declared `pr_url` optional without a literal default (`TASK_DEFAULT_REQUIRED`). Bounded repairs returned invalid discovery/plan action combinations (`PROPOSAL_ACTION_INVALID`); repetition 2 also attempted changes outside the permitted repair scope (`REVISION_SCOPE_CHANGED`). Each used three physical attempts and two repairs.
- `review_distractors`, repetition 1: cleanup tested presence of a task outside its permitted scope (`TASK_PRESENCE_SCOPE`). Repairs attempted changes outside the permitted semantic scope. Seven physical attempts and two repairs; one uncertain transport attempt retains its full reservation.
- `review_distractors`, repetition 3: the generated output contract required `review_result.draftId`, but shared validation could not establish that nested property. `TASK_COMPILER_VALIDATION` stopped the plan without asking the model to repair executor plumbing. Four physical attempts, no repairs. The compiler/validator boundary and its task-location mapping remain incomplete here.

The remaining gaps concern semantic input contracts, cleanup scope/presence, bounded repair and compiler output contracts. The run does not establish a need for a different architecture or removal of MCP. No fixes or replacement repetitions followed these outcomes.

## Matched-cohort measurements

These figures use the same three cases and all three repetitions, including failures.

| Cohort | Physical attempts | Median calls | Median latency | Known input tokens | Known output tokens | Exact usage complete |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Parent | 47 | 5 | 122.521 s | 138,510 | 95,640 | yes |
| Previous candidate | 41 | 4 | 150.046 s | 278,068 | 72,206 | yes |
| Stabilization | 40 | 4 | 351.654 s | 229,606 | 67,288 | no |
| TaskPlan | 31 | 3 | 100.116 s | 129,035 | 39,462 | no |

Calls, tokens and latency are separate measurements, not substitutes for correctness. The historical full-eight-case medians (parent 4, previous candidate 2.5, stabilization 2) describe a different case mix. The matched comparison above is the direct comparison for this run. Historical pilot/measured thresholds are not additional acceptance gates.

Candidate known cost is **EUR 1.6090745139**, with **EUR 2.5741884402** reserved for two uncertain HTTP attempts: an upper bound of **EUR 4.1832629542**. Those uncertainties occurred in distractor repetition 1 and French repetition 3; their logical calls subsequently completed under the pinned transport policy. No identity was manually restarted or replaced.

Campaign `flow-v9-112` now has 483 physical attempts, known cost **EUR 29.4212197620**, reservations **EUR 12.8510904280**, and cumulative upper bound **EUR 42.2723101900 / EUR 50**. All ten uncertain attempts retain their reservations. Estimates use recorded usage and pinned pricing/FX, not invoices. No allowance was reset or refunded.

## Architecture and validation

The model now generates one editable semantic `TaskPlan`; a deterministic compiler owns capability binding, business-port references, stable generated IDs, result envelopes, branch merges, projections, bounded ordered iteration and cleanup guards. `PlanningGraph` remains the executable representation. Typed choices compile locally; automatic recommendations need no extra model call. Planning storage is format 10; execution journals remain schema 9. Runtime, permission enforcement, injected integrations and recovery are preserved.

Removed: direct-graph response schemas and prompt recipes, model capability-resolution actions, graph-level model repair and revision baselines, YAML revision import, and obsolete free-form clarification DTOs/UI. See the [architecture/migration guide](../../../workflow-planning-v9.md) and [before/after diagrams and deletion inventory](../../../flow-hybrid-v9-implementation.md).

Before paid evaluation, the final local .NET suite passed **2,848 tests across 33 projects**, with five opt-in live skips and no compiler warnings. Planner coverage includes 141 tests. Python passed 287 core and 27 CLI tests. Affected frontend builds, five Flow package checks, Native AOT planning scenarios and published encrypted-persistence checks passed. The existing documented EF Core publication notices remain distinguished from compiler warnings.

Frozen-commit CI completed **32 successful checks**, four release-only skips, and no skipped required dependent job. All five workflows passed. CI covered all nine frontend builds, Python versions, package builds, Native AOT and published server persistence; log scans found no unexpected compiler warnings. Exact links and summaries are retained alongside this report.

Before freezing, a missing shared measurement helper in the host test project was fixed. A separately reproduced nested-repair regression was also fixed: invalidating a container no longer permits replacement of unaffected descendants or their control-flow scope. Both corrections preceded the live run.

Real sandboxed Copilot command edit/test execution remains unverified. Mocked workflow benchmarks do not resolve that limitation. Together with the failed correctness comparison, this keeps the PR **draft**; nothing was merged.

## Evidence provenance

`live.jsonl` contains the nine terminal result objects exported losslessly from the authoritative encrypted run records, in original cohort order. Temporary stdout and local validation manifests did not survive the environment change before reporting resumed. The encrypted records did survive, including every outcome, request identity, receipt, usage reservation and original summary. Reporting recovered them read-only; it did not restart the runner or dispatch another request.

`comparison.json` applies the frozen best-baseline rule. It retains the original comparison and read-only historical closed-session accounting proofs. These proofs change detached usage measurements only; failed outcomes and original encrypted records remain unchanged. `run-inspection.json` retains request IDs and sanitized findings. `freeze.json` reconstructs the frozen metadata from the unchanged source, journal and pre-dispatch transcript and explicitly identifies that provenance.

`campaign-before.json` is the retained stabilization closing snapshot, matching the pre-dispatch accounting check. `campaign-after.json` captures the completed cohort. Their deltas agree with all nine outcomes: 31 physical attempts, 129,035 known input tokens and 39,462 known output tokens. `ci-checks.json`, `ci-runs.json`, `ci-log-proofs.json` and `ci-validation-summary.txt` retain remote validation evidence. `validation.json` distinguishes those durable proofs from local checks reported in the pre-evaluation transcript.

All prior parent, candidate and stabilization evidence files remain byte-for-byte unchanged. The evidence commit contains reporting only; the evaluated implementation remains `78b8b34`.
