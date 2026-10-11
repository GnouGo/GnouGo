# Compact observations and reliable workflow execution

The architecture remains LLM → business TaskPlan → deterministic compiler → PlanningGraph → YAML. No additional planning phase, executor or intermediate representation is introduced.

Latest provider-backed evaluation: [cohort observations4](evidence/compact-observations/observations4.json), frozen on `869799e1`, attempted all six repetitions and passed **0/6 execution oracles**. The two compiler corrections below pass local validation; live acceptance remains unmet. PR #117 stays draft and the conditional dead-code pass is deferred.

Fresh requirements declare the accepted public output names and types alongside inputs. The compiler checks that the final output interface implements them; changes require explicit revision and fresh approval. Optional absent declarations remain omitted in historical serialization. This is interface validation, not proof that arbitrary prose was implemented. Independent execution oracles remain authoritative.

Browser `format: observation` returns bounded visible records with DOM grouping, selectors and observed URLs, preserving the existing content field. The response caps, explicit truncation and snapshot continuation are documented in [Browser MCP](../src/GnOuGo.Browser.Mcp/README.md). No model summarizes or interprets the page during capture. Planning prefers narrow observations and several independently verifiable agent tasks with cumulative budgets. Missing command capabilities require an existing compatible approved operation or an explicit limitation.

Optional `gnougo.artifacts.locations` extends the existing version-1 artifact metadata:

```json
{
  "version": 1,
  "locations": [{
    "pointer": "/filePath", "outputPointer": "/filePath",
    "kind": "file", "action": "materialize", "space": "file://producer/workspace/"
  }]
}
```

Input pointers must resolve required string contracts. `kind` is `file`, `directory` or `handle`; `action` is `use`, `materialize` or `release`. File/directory spaces are absolute base URIs; handle spaces are opaque namespaces. An optional output pointer relates a materialized resource to its declared output. Optional `selectorPointer` and `selectorValue` select one existing literal input-schema discriminator branch. Invalid or ambiguous declarations are rejected during discovery. Document writing publishes its file relationship; configured Cmd parameters publish only explicitly declared relationships. Names and scripts never imply one.

Existing graph validation checks known addresses for use after release and returned artifacts inside released directories, following identity-preserving exports and exclusive branches. Unknown/dynamic addresses remain an explicit review limitation. Metadata neither grants deletion permission nor establishes filesystem existence, ownership, symlink identity or completion of business work. Tools without this metadata remain usable. Requested reports must actually be materialized outside released temporary resources and verified independently.

A consumed sequence, loop, branch, parallel scope or group call must declare business exports. A live proposal omitted its loop exports and passed the resulting empty object into a workbook transform. Shared semantic binding now rejects that read with `TASK_EXPORT_REQUIRED` at the consumer, before mapping generation. It never selects an implicit last result. Effect-only scopes, explicit empty business values, dependencies and presence checks retain their existing behavior; saved YAML is not rewritten.

Executor cleanup retains public APIs. `workflow.execute` already delegates exact approved-artifact execution to `WorkflowCallExecutor`; `workflow.plan` remains the leased planner/interaction adapter. `workflow.route` shares the existing scoped child execution, journal, mapping cache and cumulative limits, removing configuration copies and duplicate dispatch loops. It no longer substitutes the first candidate for an invalid model selection. `mcp.list` retains its cache and output contract; its obsolete mandatory planning instruction is removed. `sequence` retains its runtime scope.

Learned mapping profile 4 rejects comparisons/truthiness of opaque observed tokens. Use `m.test(token, pattern)` for text predicates and `m.has` for presence, then return observed tokens. The existing one-generation/one-repair limit and source-grounding checks are unchanged. Cache identity includes the profile version. A local workbook oracle caught an otherwise well-typed empty extraction caused by a token/string comparison; the oracle remains unchanged.

## Reproduction

```sh
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter FullyQualifiedName~LocalProductOutcomeExecutionTests
```

The product fixture uses real Flow, local HTTP, Chromium and Document MCP processes, deterministic inference, and independently inspected XLSX cells. Provider-backed evidence is recorded separately. Historical benchmarks, saved workflows, approvals and uncertain external invocations are unchanged.

## Validation evidence

[Deterministic measurements](evidence/compact-observations/validation.json): 4,223 solution tests passed with `-warnaserror`, zero failures, 12 explicit platform/paid-test skips. Nine product cases passed against published Browser and Native AOT Document processes; each used one planning call, two discovery reads and zero repairs. Runtime inference was a deterministic adapter, not a provider call. The independent workbook oracle retained expected navigation failures, denied writes and collection-limit rejection.

The local page produced 15,109 serialized observation characters across all pages versus 99,060 HTML characters (84.75% reduction, 74 records). This measures representation size, not verified provider token savings. Generation-rule source text increased from 2,166 to 2,220 characters. Executor implementation shrank by 126 net lines; no new executor was introduced.

Release packages, planning Native AOT, Cmd/Document Native AOT publication, Browser publication, trimmed Server encrypted recovery, the production frontend build and Designer browser checks passed on macOS arm64. Windows-only command tests and paid Copilot unit-suite cases remain separately skipped. Published command tests passed 93 cases. The first AOT smoke exposed an outdated synthetic requirements response; its output declaration was updated and the complete smoke passed.

Live evaluation uses the existing frozen harness/oracles and `schema-portability-20261002` campaign. Read its ledger before dispatch. Use fresh run IDs and keep prior uncertain invocations stopped:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll --schema-portability inspect --campaign schema-portability-20261002 --workspace /path/to/workspace
# On the clean frozen candidate, run readiness for both cases, then planning.
# Review the retained artifact before execute with its exact --artifact-hash.
# Use --schema-portability mapping --run <fresh-id> for the untyped matrix.
# Use --schema-portability report --cohort <cohort> for comparable execution evidence.
```

Neither compilation nor deterministic local execution establishes success on Amazon.fr or the pinned external repository. Live results will be retained separately; the PR remains draft until its execution gates pass.

## Live result: incomplete

[Retained live evidence](evidence/compact-observations/live.json), candidate `a16f12a3f8e838c72c9d235fd98f33e2b886caa0`: the first untyped-MCP planning request stopped after 1,215,369 ms, one logical planning call, four physical transport attempts, one discovery read and zero repairs. Three attempts returned HTTP 500 with gateway code `ESG120` and message `Routing failed`; the fourth has no verified completion. The session correctly stopped with `MODEL_DISPATCH_UNVERIFIABLE`. No request was replayed or reconciled.

There are no verified usage receipts for this request. The ledger retains €35.45388838 of verified historical cost plus €1.30337639 reserved for unknown completion, for a €36.75726477 upper bound under the €50 ceiling (about €13.24 remaining). Unknown tokens/cost are not reported as zero. Paid dispatch stopped after the repeated external gateway failure.

**Acceptance remains unmet:** 0/9 untyped execution cases completed; the three Amazon and three code evaluations were not started. No execution success is claimed. Local readiness passed Browser cleanup, independent XLSX reading, code MCP discovery and toolchain availability, but does not establish external execution. Restore the configured provider gateway route before collecting a fresh cohort, preserving the unresolved request and its reservation. Keep PR #117 draft. Historical 33/33, mapping and failed live cohorts remain unchanged.

[CI status snapshot](evidence/compact-observations/ci.json) distinguishes completed checks from still-running jobs.

## Fresh provider check and retained cohort

The [fresh availability check](evidence/compact-observations/availability-observations2.json) succeeded with one provider attempt, 51 input tokens and 11 output tokens. Before dispatch, the old exhausted request was explicitly retained as inconclusive under the campaign lease. It remains permanently non-dispatchable, with unchanged evidence and full unknown-usage reservation. Two earlier checks made no provider dispatch: campaign admission blocked the first; budget preflight timed out on the second.

[Cohort observations2](evidence/compact-observations/observations2.json) remains incomplete, 0/6 execution oracles. The first Amazon proposal correctly failed the nullable-output check. Review of the second exposed a consumed loop without exports; it was not executed. The first code run failed because the operator submitted `answer` instead of the required `response` for a choice interaction. The harness rejected that answer and the runtime stopped with `RUN_NEEDS_RECONCILIATION`, blocking cleanup. That invocation and checkout remain untouched. The code proposal also named the review-text input without binding its value and asked its delegated agent to write outside the project root; compilation does not establish business completeness.

The missing-export defect reproduced in seven generic scope cases. The four-line semantic check passed [851 planner tests and 4,237 solution tests](evidence/compact-observations/scope-export-validation.json), with zero failures, 13 explicit skips, the frontend production build, Release package and Native AOT smoke. A separate frozen cohort is required after this correction. Prompts, execution oracles, runtime permissions and mapping machinery are unchanged; no final dead-code pass is justified before the live gate passes.

## Computed-binding correction

[Cohort observations3](evidence/compact-observations/observations3.json) retained three unsuccessful executions on `7d4e0df2`; the other three repetitions remain in the six-run denominator. The first Amazon run correctly rejected an unobserved required consent selector, including a proposed null without an authoritative default. The second exposed a compiler defect: a whole-value constraint guard wrapped `json(...)` inside `checkedMapping`, whose source must be structural. Browser cleanup succeeded in both runs; no workbook was produced.

Final lowering now assembles the ordinary `{ value: computedValue }` envelope for whole-value checks of typed JSON encoding and predicates. Existing `set.output_schema` validates the complete computed result before downstream execution. Structural selections, per-item projection, the runtime sandbox and learned mappings are unchanged. Invalid encoded values still fail their authoritative constraints; the compiler does not reinterpret them as plain strings. Generic execution regressions cover valid and invalid values with arbitrary operation names and no inference.

The code run returned no verified MCP completion receipt after 27 interactions and 16 SDK inference attempts. Its encrypted logical checkpoint retains partial evidence and 1,893,385 conservatively reserved tokens; this does not establish the exact interruption cause. Flow blocked cleanup, and the invocation and checkout remain untouched. The generated delegated prompt also omitted installation and the declared complete check suite. Partial command evidence and a local temporary report cannot pass the existing oracle. The project-selected pnpm version was independently checked as 10.34.5; this readiness observation is not workflow execution evidence.

[Computed-binding validation](evidence/compact-observations/computed-guard-validation.json): all three new execution regressions failed before the correction and pass afterward. The full solution passed 4,240 tests, zero failures and 13 existing skips with `-warnaserror`, including 854 planner tests. Release packaging, planning Native AOT, the frontend production build and skill validation passed. The live gate remains unmet; no dead-code pass was performed.

## Frozen cohort observations4

The provider recovered. All six fresh repetitions used candidate `869799e186c6e9b1e9906e0231e32661b3c04e6e`, the same prompts, environment, limits and `real-workflows-v1` oracles. Five reached execution; one stopped during planning. No previous invocation was replayed or reconciled. One HTTP 500 during Amazon repetition 3 recovered within the existing retry policy; there were no provider-schema rejections.

| Repetition | Planning calls / repairs | Execution result |
| --- | --- | --- |
| Amazon 1 | 3 / 0 | Workflow completed; independent workbook oracle failed. |
| Amazon 2 | 2 / 0 | `REQUIREMENTS_OUTPUTS_CHANGED`; execution did not start. |
| Amazon 3 | 2 / 0 | Workflow completed; independent workbook oracle failed. |
| Code 1 | 4 / 1 | `RUN_NEEDS_RECONCILIATION`; report missing, cleanup blocked. |
| Code 2 | 3 / 0 | `RUN_NEEDS_RECONCILIATION`; report missing, cleanup blocked. |
| Code 3 | 2 / 0 | `RUN_NEEDS_RECONCILIATION`; report missing, cleanup blocked. |

Both Amazon executions received compact observations with continuation cursors and explicit truncation. Neither generated plan followed the continuation or visited product pages. Each wrote a note row instead of product rows; independent XLSX inspection rejected both. Amazon 3 exercised one cold `mapping.dynamic` generation with zero repairs: its source-derived note and empty collection passed shape validation, but failed the business oracle. This establishes neither extraction completeness nor a cache/repair matrix pass. Browser cleanup succeeded.

All code executions installed dependencies and ran actual checks in disposable pinned checkouts. Encrypted SDK checkpoints retain partial command results, including failed tests and unavailable prerequisites; sanitized command/exit-code evidence is included in the cohort record. None returned a verified final MCP completion receipt or reached the final report write. Flow correctly blocked cleanup. The checkpoints contain respectively 17, 12 and 12 inference attempts, with 1,947,718, 1,870,330 and 1,958,978 conservatively reserved tokens. These observations do not establish the precise interruption cause. The invocations and checkouts remain untouched. Requests to write outside the approved checkout or alter shared toolchains were refused; no persistent grant, sandbox expansion or GitHub publication was authorized.

Generated delegated prompts also sometimes name `reviewText` without supplying its value. Code 1 and 2 bind it to a later dedicated review call, which execution never reached; Code 3 supplies it only to the later assembly transform. Static contract validation does not prove that requested prose has been implemented. These composition and external-completion limitations remain open; the oracles and permission checks were not weakened.

| Measured work | Logical calls | Physical attempts | Verified input / output tokens | Cost |
| --- | ---: | ---: | ---: | ---: |
| Planning | 16 | 17 | 217,864 / 38,230 | €1.992178 |
| Execution inference | 49 | 49 | 1,242,004 / 22,784 | €6.141238 |

Planning used 15 discovery reads and one repair. Median/p95 planning latency was 82.0/355.5 seconds for Amazon and 127.3/149.7 seconds for code. Observed execution latency was 62.7/74.3 seconds for Amazon (two executions) and 1,096.8/1,154.7 seconds for code (three executions). The evidence includes token/cost medians, sample counts and nearest-rank p95; unstarted execution is not counted as zero latency. All six repetitions remain in the success denominator.

Paid dispatch stopped after the cohort. The campaign records **€47.331176 verified cost plus €1.303376 reserved for historical unknown completion**, a **€48.634552 upper bound** under €50. The ceiling was not exhausted; no additional paid iteration is included. Current-cohort provider usage is verified even where external MCP completion is unknown. Historical 33/33 and all earlier failed cohorts remain unchanged and are not pooled into this result.

The current candidate passed [22 CI checks, with four skipped and none failed](evidence/compact-observations/observations4-ci.json), in addition to the 4,240-test local solution run, Release planning package, Native AOT smoke and skill validation. The two live-derived compiler fixes add nine net production lines; no framework, representation, executor or scenario rule was added. No further production changes or dead-code cleanup were made after freezing this cohort.

Reproduce the retained report without dispatching inference:

```sh
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability report --cohort observations4 \
  --campaign schema-portability-20261002 --workspace /path/to/workspace
```

The encrypted journal contains the original schemas, responses and execution evidence. A future authorized evaluation must use new identities and a matching frozen manifest; never reuse these execution IDs or resume an uncertain invocation automatically. See the [benchmark commands and accounting rules](../tests/GnOuGo.Agent.Planning.Benchmark/README.md).

## Separate deterministic corrections after observations4

See [browsing review and Copilot receipts](browsing-review-and-copilot-receipts.md) for the subsequent local regressions and corrections. They do not modify this cohort, its oracles or its live success rate. No uncertain invocation was resumed.
