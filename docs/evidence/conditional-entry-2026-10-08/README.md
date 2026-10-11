# Conditional entry requirements

The [retained proposal and counterexample](../indexed-projections-2026-10-08/review-rejection.json) entered an optional-action branch without establishing its action's `requires`. The branch also performed a subsequent observation, so correcting its entry requires an explicit revision. The rejected artifact, receipts and exhausted planning session remain unchanged.

TaskPlan preflight now checks entry-known requirement conjuncts against enclosing branch facts and authoritative contracts. Diagnostics identify the controlling condition, affected operation, requirement and scope. Values first observed inside the branch still use runtime assertions. A nullable boolean unequal to `true` is not assumed to equal `false`; unrelated flags, producers and loop items establish no relationship. Captures, declared exports and group arguments preserve only explicit identities. No runtime, mapping, permission, token-limit or lowering-profile change is included.

Automatic repair receives an exact condition replacement only for an isolated optional action. Its original condition and every `requires` remain intact. Shared operations, inference, active alternatives, cleanup and contradictions require explicit revision. Issued historical schemas and authorities remain unchanged. These checks establish the supported predicate implication, not arbitrary business completeness.

## Reproduction and validation

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
env Kestrel__Endpoints__Grpc__Url=http://127.0.0.1:0 dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror -p:SkipClientBuild=true --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~PlanningRecoveryTests|FullyQualifiedName~RequirementReview'
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability replay-compile --workspace /path/to/workspace --campaign schema-portability-20261002 --run indexprojection20261008a-amazon-1
```

The last command is read-only: it compiles the original retained plan using its retained contracts, without inference, modifying the session or executing external work. Current verification results and fresh live evidence are recorded separately alongside this report. The historical 33/33 benchmark remains unchanged.

## Completed deterministic gates

Frozen production/harness commit: `c1c3de014d1909e46638c38d1cda849c030e48f6`. [Validation](validation.json) records **4,912 passed, 13 skipped, zero failures** across 33 projects in the full solution with `-warnaserror`. This includes 1,171 planning tests and 707 host tests. The separately selected guard/scope/repair gate passed 80 tests; local execution/recovery passed 34 tests, including the real Browser/Document fixture and independent XLSX assertions. All 39 additional planner regressions pass. No assertions, limits or timeout values were relaxed.

[Read-only replay](retained-replay.json) rejects the historical proposal with three `TASK_CONDITIONAL_REQUIREMENT` diagnostics, zero inference and zero repairs. Two identify the optional-control branch; the third identifies a missing search-resource guard. Original sessions, requests, receipts and artifacts were not changed. The replay worktree differed only by a generated metadata comment timestamp; it was restored before the clean live freeze.

[Release validation](release-validation.json) passed planning packaging with `-warnaserror`, planning Native AOT including the new guard smoke, and published CLI encrypted recovery. The CLI Native AOT publish retains two existing upstream EF experimental-feature notices; that publish command does not use `-warnaserror`. Skill validation passed. The full build also completed its existing frontend production build.

## Fresh live boundary

Only one fresh Amazon validation is authorized, with new identities, the existing ten-product maximum, unchanged oracles and the shared EUR 150 campaign ceiling. Pricing/currency readiness and focused deterministic execution must pass first. Concrete revision/hash-bound artifact approval and requirement acknowledgments remain required. Unknown reservations remain retained; uncertain invocations are never replayed. No code-review run or cohort expansion is included. PR #117 remains draft pending its full execution gates.

## Fresh planning and approved execution

Cohort `conditionalentry20261008a`, run `conditionalentry20261008a-amazon-1`, maximum ten products. [Frozen binaries/oracles](frozen-binaries.json), [manifest](manifest.json), [pricing/currency readiness](provider-readiness.json) and [disposable execution readiness](execution-readiness.json) are retained. Source and harness remain the same committed candidate.

The first proposal stopped on a nullable file-result mismatch. Subsequent proposals needed explicit compaction, typed-index and lookup corrections. Revisions 7 and 9 demonstrate the new check live: a search action required `not(captcha_detected)`, but its shared branch omitted that predicate. The compiler reported `TASK_CONDITIONAL_REQUIREMENT` and `REVISION_REQUIRED` without spending an automatic repair. The exact corrected nested predicate appears in revisions 11 and 13; all runtime assertions remain. Rejected proposals and revision commands are preserved alongside their reviews and in the encrypted campaign.

Revision **13**, artifact **`8f2d6c67cb2318e8c8bafe6124e1f5ac4eacfbbf1712ed5fdb76f0b3fb0bbc6e`**, passed compilation and concrete review. The user explicitly acknowledged all six requirements and approved one execution; see [review](amazon-review.md) and [submitted approval](approval-command-r13.json). Eight planning calls, two discovery reads, zero automatic repairs and five explicit revisions took 734.881 seconds; known usage was 146,083 input / 48,816 output tokens, costing EUR 1.947728. This does not meet a minimal-call target; accepted business work was preserved rather than hiding validation failures.

## Single approved execution: oracle failed

The approved run completed in **44.631 seconds** and the runtime reported success, but the independent execution oracle **failed**. The first Browser acquisition returned zero records, an empty title and no HTTP status, with `success: true`, no truncation and no invalidation. There is no captured CAPTCHA or denial evidence. These receipts do not establish whether the empty document was transient, unavailable or captured too early; the cause remains unresolved. See [observed entry](observed-entry.json).

The workflow found no search control, skipped search and product visits, wrote one explicit missing-search/blocker row, and closed the Browser. [Independent XLSX inspection](xlsx-inspection.json) confirms the workbook contains that status row, not product observations. The unchanged oracle reports `product_visits_do_not_match_rows` and `workbook_value_not_supported_by_captured_product_page`. Browser cleanup succeeded. The two final MCP calls are the oracle's closure check and harness disposal, separate from the workflow's three calls.

[Execution evidence](execution-result.json) records **three interpretation calls**, 550 known input / 795 output tokens, **zero dynamic mappings** and **zero product visits**. Runtime inference cost EUR 0.023605; total attempt cost EUR 1.971333. Total planning plus execution latency: 779.512 seconds. Campaign upper bound: **EUR 112.681591 / 150**, including unchanged **EUR 5.203327** unknown reservations. No new uncertain provider attempt occurred. The runtime's successful status is not business or oracle success.

This live confirms the corrected proposal can compile and its guarded path can be skipped safely, but does not validate successful product extraction or mappings; those paths were not reached. The next investigation belongs at the Browser acquisition/readiness boundary and the workflow's explicit missing-observation outcome. Empty snapshots are not automatically invalid in general, so no blanket prohibition or site-specific workaround was added. No further paid run, uncertain replay, code-review evaluation or cohort expansion was performed. [Cohort report](cohort-report.json): zero of six required execution oracles passed; only the one authorized Amazon slot ran. PR #117 remains draft.

## CI boundary

Local full solution, package, planning AOT and encrypted recovery checks passed. One remote ProxyCopilot Linux ARM64 AOT job was cancelled at its existing 30-minute ceiling while `apt-get update` was waiting for Ubuntu package metadata, before repository compilation. The same job was retried unchanged. No CI timeout, assertion or source was relaxed. Current remote statuses are retained in `ci-status.json`; pending checks are not reported as passed.

At evidence publication, frozen-candidate CI had **27 successful, four skipped and two still-running checks**. The unchanged Linux ARM64 retry passed. The remaining jobs are Agent Server tests and deterministic planner validation; neither is counted as passed remotely. The complete local solution and scoped publish/recovery checks above have finished successfully.
