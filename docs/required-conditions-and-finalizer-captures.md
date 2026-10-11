# Required conditions and guarded finalizer captures

This correction addresses the two retained generation failures in
[cohort contractsreview20261004e](contracts-and-review-corrections-2026-10-04.md).
It preserves the planning architecture, runtime executors, `mapping.dynamic`,
permissions, execution oracles and historical uncertain invocations.

## Required business conditions

An optional TaskPlan `requires` value expresses a Boolean condition that must hold
before a task begins. False fails the task; it does not skip requested work and
produce a substitute output. The compiler checks the condition's type, dependencies
and availability, then emits an existing checked `set` before inputs, runtime
inference or effects. Predicates remain structured until final YAML lowering.
Selected-field checks on the right of `and`/`or` retain short-circuit behavior.

For example, a publication sequence may require observed completeness and return
its actual written path. Ordinary `conditional` tasks still need matching explicit
branch outputs; the compiler never invents a workbook path for an incomplete branch.
Checking a flag does not prove the quality of an observation: requirements review,
continuation handling and independent execution oracles remain necessary.

New model-facing schemas include nullable `requires` on tasks. Stored TaskPlans omit
it when absent, preserving historical serialization and hashes. Issued schemas and
saved artifacts are not rewritten. Scoped repair cannot add, remove or alter a
precondition, including by replacing or removing its task; use an explicit revision.
Review diagrams display the condition. Prompt presentation omits duplicate schema
descriptions and default `required: true` port flags without removing constraints or
authoritative descriptions from operation context.

## Finalization across lexical scopes

Nested finalizers may inspect `present` for an available preceding lexical ancestor.
The compiler captures presence separately and snapshots a payload only if present,
using existing switches, sets and workflow inputs. An omitted internal payload is
not a business null. A successful producer containing null still has a present result.
Checked consumption occurs inside the selected branch, retaining artifact origin.
Sibling branches, unavailable later ancestors and reusable group boundaries remain
inaccessible.

To preserve a report after verified failure, put reporting in an `always` sequence
and cleanup in that sequence's nested `always`. A failed finalizer stops its ordinary
sequential block; the nested finalizer is what permits cleanup after report failure.
Unknown completion still blocks finalization and replay. Presence proves neither
payload non-nullability nor successful external work.

## Reproduction and checks

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror \
  --filter 'FullyQualifiedName~LocalProductOutcomeExecutionTests|FullyQualifiedName~ReportFinalizationExecutionTests'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Core -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 \
  --self-contained true -m:1 -warnaserror
tests/GnOuGo.Flow.Planning.Smoke/bin/Release/net10.0/osx-arm64/publish/GnOuGo.Flow.Planning.Smoke
```

`TaskPreconditionTests` and `FinalizerCaptureTests` exercise real Flow execution with
deterministic adapters, absent/null payloads, short-circuit reads, sequential/parallel
loops, effect exclusion, nested cleanup, artifact origin and restricted repairs.
The local Browser/Document fixture independently inspects visited pages and XLSX cells;
an incomplete observation fails before extraction inference and publication.
The Native AOT smoke covers serialization and both condition outcomes.

The retained code proposal can be compiled without inference or saved-session changes:

```sh
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability replay-compile --campaign schema-portability-20261002 \
  --workspace "$GNOU_GO_WORKSPACE" --run contractsreview20261004e-code-1
```

The original Amazon proposal retains its invalid asymmetric conditional interface.
It needs an explicit revision; this correction does not rewrite or approve it.
Deterministic validation and retained-proposal replay are separate from live-provider
acceptance. Fresh evaluations must use new IDs, the same oracles and the existing
campaign spending gate. Historical benchmarks remain unchanged.

## Retained first candidate

Candidate `0cbc626b0e3d3e150f91306d069f0dc7c1a2b200` passed 4,418 solution tests
with zero failures and 13 skips, including 926 planner tests. The separately enabled
Designer browser smoke also passed. Release packages, Native AOT and published
encrypted recovery passed. [Validation evidence](evidence/required-conditions-2026-10-04/candidate-a-validation.json)
keeps overlapping counts separate.

[Fresh cohort a](evidence/required-conditions-2026-10-04/preconditions20261004a.json)
remains **0/6 execution oracles, incomplete**. Amazon reached review in three calls
without repair, but omitted consent and continuation handling. Its explicit revision
exposed a final-lowering defect: a checked computed envelope was emitted as one
JavaScript object expression, hiding its field shape from final YAML validation.
A two-branch regression reproduces the exact `EXPR_TYPE_MISMATCH` without inference.
The correction retains the envelope as structured YAML and compiles only its value;
it changes neither validation constraints nor the runtime.

Code-review first selected a field from an already selected Boolean port. Its revision
retained missing scope exports and cross-scope references after two repairs. Those
proposals remain rejected. [Explicit review feedback](evidence/required-conditions-2026-10-04/candidate-a-reviews.json)
preserved accepted requirements in both sessions. No artifact was approved, no workflow
ran, and no new Copilot SDK invocation began. Four repetition slots were unattempted.

These separate failed runs used nine logical planning calls, ten physical attempts,
two repairs and two review revisions, costing **€1.184900**. Campaign committed/reserved
usage became **€56.247367/€100**, including the unchanged **€1.303376** historical
reservation. New usage is verified. Execution and runtime inference remain unmeasured.
No uncertain invocation was resumed. A corrected candidate needs a fresh cohort;
these failures must not be pooled into its success rate.

## Corrected candidate and live execution

Frozen production and harness commit `df47ec31640e7fb0ba6a275780639c5ab6ff2642`
passes **4,420 solution tests, zero failures and 13 skips**, including 928 planner
tests. The final-lowering regression and arithmetic selection passes 55 tests.
Release packaging, planning Native AOT and published encrypted recovery pass.
The 22 Copilot logical-boundary and 10 real-transport receipt regressions also pass
after inspecting the live failure. Counts overlap; do not add them together.
[Validation and log hashes](evidence/required-conditions-2026-10-04/candidate-b-validation.json)
include successful [planner CI](https://github.com/GnouGo/GnouGo/actions/runs/37214926807)
and [build/package CI](https://github.com/GnouGo/GnouGo/actions/runs/37214926974).
One version-tag API lookup job was retried without changing source.

[Cohort b](evidence/required-conditions-2026-10-04/preconditions20261004b.json)
is **0/6 execution oracles, incomplete**. Requirements review and execution are
reported separately:

| Run suffix | Planning | Execution and unchanged oracle |
| --- | --- | --- |
| `amazon-1` | Seven logical calls, eight physical attempts, one repair, three explicit revisions | Not approved: extract-only output still requested a synthesized status without a declared default. Its planning allowance is exhausted. |
| `amazon-2` | Five calls, one repair, two revisions; approved after correcting continuation metadata | Actual Browser reads consumed three continuations. A cursor still remained, so `requires` failed before product selection/extraction or XLSX writing. Browser cleanup passed. No cookie control was detected initially; live consent acceptance and product/XLSX coverage remain unverified. |
| `code-1` | Three calls, zero repairs, one revision; approved | Operator supplied bare `true` instead of `{"response":true}` to confirmation. Rejected before external calls; retained as failed and never replayed. |
| `code-2` | Three calls, zero repairs, one revision; approved | Real pinned clone/diff and Copilot commands ran. Completion remained unverified; reporting and cleanup were blocked. |
| Both `*-3` slots | Not attempted | Remain in the six-slot denominator. |

The Amazon result demonstrates safe failure on an incomplete observation, **not**
successful extraction. The final snapshot still reported continuation and truncation;
no empty product result or successful workbook was manufactured. A subsequent
fresh plan needs sufficient bounded reads or an observed, narrower read scope.
Do not remove the condition or treat earlier partial observations as complete.

The encrypted Copilot checkpoint records one SDK session, 43 interactions, 49 tool
observations and 12 inference attempts. The next inference was denied because
1,915,633 conservatively reserved tokens plus its 221,003 required input tokens
would exceed the unchanged two-million-token ceiling. An idle event was observed,
but two successful shell callbacks lacked terminal exit evidence. Those callbacks
and idle alone do not establish external completion. The checkpoint retained
`completionVerified: false`; the workflow therefore correctly retained
`RUN_NEEDS_RECONCILIATION`. This run does not demonstrate a false reconciliation
diagnostic or validate the report/cleanup path live.

Attempts to read the SDK's memory-only session paths through the host shell returned
missing paths and contributed to repeated commands. That is retained evidence for
future producer-side investigation, not a reason to expose host files, add planner
rules or increase limits. One fixed `/tmp` write was individually refused. The
uncertain invocation and its checkout remain untouched; no automatic reconciliation,
cleanup or replay occurred. No review was published to GitHub.

[Explicit reviews](evidence/required-conditions-2026-10-04/candidate-b-reviews.json)
and [sanitized execution/termination evidence](evidence/required-conditions-2026-10-04/candidate-b-execution.json)
retain the failures independently from candidate a. Third repetitions were not
dispatched after these blockers; this is incomplete coverage, not budget exhaustion.
PR #117 stays draft.

### Measured accounting

| Stage, cohort b | Logical calls | Physical attempts | Repairs | Verified input/output tokens | Verified cost |
| --- | ---: | ---: | ---: | ---: | ---: |
| Planning | 18 | 19 | 2 | 268,921 / 79,681 | €3.327425 |
| Execution inference | 14 | 14 | 0 mapping repairs | 415,939 / 8,540 | €2.080976 |

Planning used ten discovery reads and seven explicit review revisions. One planning
attempt has unknown usage and retains a **€1.303376** reservation. All recorded
execution inference usage is verified. Planning latency is retained per run in the
report; actual Amazon execution took 95.496 seconds and code-review execution took
1,130.805 seconds, including interaction. The confirmation-only failed run took
39.013 seconds. These failed runs do not establish a before/after success or latency
improvement; the historical cohorts and 33/33 benchmark are unchanged.

Final campaign accounting is **€60.352392 verified + €2.606753 reserved =
€62.959145/€100**, leaving €37.040855 under the authorized ceiling. No unknown
reservation was released. No further paid dispatch accompanies this report.

### Reproduce inspection and collect separately

Use the frozen commit, the manifest's provider/configuration and the same toolchain
environment (Node 24.20.0, pnpm 10.34.5, Python 3.11.13). The manifest pins the
environment, harness tree, prompt hashes and unchanged oracle version. Build the
benchmark and current MCP binaries before collection. These inspections are read-only:

```sh
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability report --campaign schema-portability-20261002 \
  --workspace "$GNOU_GO_WORKSPACE" --cohort preconditions20261004b
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark -- \
  --schema-portability inspect-run --campaign schema-portability-20261002 \
  --workspace "$GNOU_GO_WORKSPACE" --run preconditions20261004b-code-2
```

New collection must use unused cohort/run IDs, recheck the campaign journal, and
retain its own manifest. Submit implementation feedback through `--schema-portability
revise --revision-command <file>` with `preserveRequirements: true`. Review the
resulting requirements, loop bodies, conditions, dependencies and finalizers before
supplying `execute --review-command <file>` with exact revision/hash and explicitly
reviewed requirement IDs. Confirm-mode runtime forms return `{"response":true}`;
choice-mode forms return `{"response":"<selected option>"}`. Do not automatically
acknowledge requirements or approve subsequent permission requests. Started and
uncertain runs above must never be replayed by these commands.
