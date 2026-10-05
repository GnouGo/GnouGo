# Bounded independent collection mappings

The candidate extends the existing `mapping.dynamic` binding. The planning architecture, executor set, historical transforms and stored artifacts are unchanged. Global summaries and code review are not partitioned.

## Change

Extract transforms may explicitly declare `each: { input, output }`. The input names an array; the sole output field is an array. Every original item produces exactly one output item, preserving order, duplicates and nested arrays. Other bound inputs remain read-only context. Scoped repair cannot introduce or change this declaration.

Generation uses complete, deterministic examples within verified request limits. Execution checks every original item with one shared sandbox allowance, then validates the assembled result before publication or caching. One generation and one repair cover the entire invocation, including invalid cache recovery; durable receipts and cumulative budgets remain authoritative. Unknown completion is never replayed.

Compiled projections select physical export paths before materializing logical envelopes. The structural-expression path selects only requested values instead of importing unrelated loop snapshots. Typed mappings require no inference. The live observation reader now joins only completely consumed, matching snapshots; existing visit, cell-value and cleanup assertions remain unchanged.

## Reproduction

```sh
dotnet test tests/GnOuGo.Flow.Tests -m:1 -warnaserror --filter FullyQualifiedName~DynamicMapping
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror --filter FullyQualifiedName~DynamicMappingCompilation
dotnet test tests/GnOuGo.Agent.Server.Tests -m:1 -warnaserror --filter 'FullyQualifiedName~LocalProductOutcomeExecution|FullyQualifiedName~LiveObservationOracle|FullyQualifiedName~BenchmarkCampaign'
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror
```

See [runtime semantics](runtime-mappings.md) for the optional contract and migration boundaries. Local model adapters are deterministic execution evidence, not paid-provider evidence.

## Deterministic validation

The full solution passed **4,473 tests**, with zero failures and 12 existing Windows-only/opt-in skips, across 33 projects using `-warnaserror`. This includes 1,104 runtime tests, 937 planner tests and 650 Server tests with the browser review smoke enabled. The final focused Server/harness rerun passed 75 tests. Real local Browser/Document fixtures verify product visits and exact XLSX rows in sequential and parallel extraction modes; generation takes one planning call and zero repairs.

The frontend production build and planning-skill validation pass. Release packages for AI.Core, Flow.Core, Planning, Integrations and Persistence pass. The planning Native AOT smoke also passes, including collection extraction, optional declaration serialization and actual local MCP transport. The trimmed published Server encrypted recovery smoke passes. [Validation counts and retained log hashes](evidence/bounded-collection-mappings-2026-10-05/validation.json) include the initial fixture failure.

The initial new parallel fixture incorrectly assumed sequential HTTP arrival order. It now checks the exact visited products without ordering concurrent requests; workbook row ordering and all existing fixture oracles remain unchanged. The failed initial run is retained separately from the passing validation.

## Live validation

Campaign `schema-portability-20261002` remains capped at €100. The pre-dispatch ledger records €71.595429582704 committed or reserved, including two retained unknown attempts. Fresh Amazon and code-review identities are required; no historical invocation is resumed. Only one attempt per scenario is collected initially. The six-slot acceptance report stays incomplete unless all execution gates pass on the same frozen candidate.

Validation results and sanitized live evidence are recorded after collection. Historical cohorts, the historical 33/33 benchmark, and their oracles remain unchanged.

## First fresh cohort: retained failures

Candidate `902219b18985667248d782b52280f8c94e8e3d1e`, cohort `collections20261005a`: **0/6 execution oracles**. Only repetition one of each case was attempted; later slots remain unexecuted.

- Amazon stopped before execution after five planning calls, two repairs and one explicit revision. The revised proposal put a task ID in `each.input` and supplied multiple scalar results rather than the sole array result. The compiler rejected it; no product visits or XLSX are claimed.
- Code reached approved execution after five planning calls and three explicit review revisions. Clone and pinned comparison completed. The test operator submitted the wrong JSON wrapper to a permission prompt (`answer` instead of the choice contract’s `response`). The console host rejected it, interrupting the MCP call; one provider inference completed, but no command completion was established. The run remains uncertain, with cleanup blocked and no final report. It has not been resumed or reconciled. This is not evidence of a Copilot completion-contract defect.

The campaign upper bound after these attempts is **€73.200819337715/€100**, including the original €2.606752783964 unknown inference reservations. [Six-slot report](evidence/bounded-collection-mappings-2026-10-05/cohort-a.json), [Amazon proposals](evidence/bounded-collection-mappings-2026-10-05/cohort-a-amazon.json), [code execution and classification](evidence/bounded-collection-mappings-2026-10-05/cohort-a-code.json). Raw receipts and observations stay encrypted.

The follow-up constrains the new generation schema to one nonnullable array result and states the `each` business contract in the existing prompt, since schema compaction removes descriptions. Private definition names lose a redundant prefix to keep the existing input limit; no limits or authoritative contracts change. Three regression cases reproduce the invalid shapes against the old schema and reject them after correction. The console host now displays the expected response schema and re-prompts after invalid input, preserving explicit selection, refusal, EOF and cancellation. Neither correction changes runtime permissions or uncertain-completion handling.

## Conditional null refinement exposed by the follow-up live

The second Amazon proposal combined an explicit selector null check with another Boolean condition. Its source was a conditional export joining a nullable string and a null-only alternative. The compiler recognized only a direct comparison against a `type` array, so the valid guarded consumer was rejected as opaque. Four generic reproductions failed before correction.

The existing branch refinement now follows true conjunctions, false disjunctions and negation, removing only null alternatives from the guarded contract. It preserves other constraints and keeps checked identity projection at the consuming branch. Unrelated flags, true disjunctions, mixed nonnull types and null-only sources cannot establish a string contract. The regression executes all input/branch combinations through Flow without inference and verifies that the logical plan remains unchanged. No operation-specific rule, runtime executor or repair authority is added.

## Second frozen cohort and producer correction

Candidate `757860b4c25752711b7ef1ecc98500ceb414853e`, cohort `collections20261005b`: **0/6**, with only repetition one attempted. Amazon initially reached review in two calls with no repairs but lacked continuation coverage. Explicit revisions exposed the guarded-null compiler defect above. Its final retained state used seven logical/eight physical planning attempts, two repairs and three explicit revisions; no execution was authorized.

Code reached review in five calls, no repairs and two explicit revisions. Actual clone and pinned comparison completed, followed by interactive project/command inspection. The legacy `code_agent_edit` path stopped without a final receipt; cleanup remained blocked and no report was produced. A real MCP client/server fixture reproduces `Server returned InputRequiredResult more than 10 times` on that entry point; the same twelve-question exchange succeeds through the existing Tasks path. The legacy entry point now reuses that bounded logical operation and commits its original typed result before disposal. No transport framework, planner rule, new allowance or automatic reconciliation is added. Parameterized receipt tests retain the existing command, failure, denial, restart and cleanup oracles for both entry points.

Campaign upper bound: **€75.371309315443/€100**, including the unchanged €2.606752783964 unknown inference reservations. The new uncertain external invocation is retained separately from provider-usage accounting. [Six-slot report](evidence/bounded-collection-mappings-2026-10-05/cohort-b.json), [Amazon proposals](evidence/bounded-collection-mappings-2026-10-05/cohort-b-amazon.json), [code evidence](evidence/bounded-collection-mappings-2026-10-05/cohort-b-code.json).

Candidate B passed **4,478 solution tests**, zero failures and 12 existing skips; Release Planning packaging, Native AOT and the rebuilt harness also passed. [Validation](evidence/bounded-collection-mappings-2026-10-05/validation-b.json). The compiler follow-up's isolated full run passed 4,485 tests with one test-host bind failure because an unrelated local container occupied port 4317. Final reruns use an ephemeral test-only gRPC endpoint; live host configuration and that external process remain unchanged. The null-refinement regression fails in four cases before the fix and passes all eight positive/negative cases afterward. The affected producer suite passes 123 tests, including the extended real-command receipt cases. Final combined validation and fresh live evidence are recorded separately.

## Final deterministic gate

Candidate **`43b5358a1b8a617b19352704d60cfde6977b574a`** passes **4,499 solution tests**, zero failures and 12 existing skips across 33 projects, with `-warnaserror`. The preceding combined run found one list-contract fixture using the old service constructor; the production host already registered durable Tasks. After correcting that fixture, all 240 Copilot MCP tests and the full solution rerun pass. Both runs remain retained.

Release Planning packaging, planning Native AOT, Copilot Native AOT and the published encrypted Copilot task-recovery smoke pass. The smoke verifies tenant isolation and stopping on unknown completion with zero inference. Earlier frontend, skill, remaining affected packages and published Server recovery checks remain applicable to unchanged implementations. The retained Amazon proposal compiles and validates on the clean final candidate with **zero calls, zero repairs and no saved-session mutation**. [Final validation and log hashes](evidence/bounded-collection-mappings-2026-10-05/validation-final.json).

## Final fresh cohort: incomplete live acceptance

Frozen cohort **`collections20261005d`**, candidate `43b5358a`: **0/6 execution oracles**. Only repetition one of each case was attempted; repetitions two and three were not started. No cohort expansion is authorized by these results.

| Measurement | Amazon | Code review |
| --- | ---: | ---: |
| Logical / physical planning calls | 5 / 5 | 8 / 8 |
| Discovery reads / repairs / explicit review revisions | 2 / 1 / 2 | 4 / 2 / 3 |
| Verified planning input / output tokens | 59,567 / 19,661 | 176,231 / 19,630 |
| Planning latency | 241,652 ms | 247,952 ms |
| Runtime inference calls | 0 | 0 |
| Execution latency | 30,460 ms | Not started |
| New measured cost | €0.790793 | €1.309626 |

Amazon initially reached review in two calls and zero repairs (73,386 ms). Review rejected guessed controls and incomplete capped observations. The approved revision uses explicit consent handling, complete page lists, actual per-product visits, independent page extraction, compact downstream values and browser cleanup. Actual execution consumed **all three matching home-page pages: 137 records, 44,114 UTF-8 bytes of observation JSON**, without capture truncation. The compiled collection projection succeeded. The mapping encountered a cold cache, then stopped before inference because the pinned deployment `gpt-5.5-2026-04-24` has no exact declared safe input allowance available to the resolver. Browser cleanup passed. Consent selection, product visits, learned mapping execution and XLSX cells remain unverified. No workbook was produced.

This is a missing provider-metadata prerequisite, **not a missing completion receipt**. The execution adapter forwards capability queries correctly; an initial adapter diagnosis was ruled out. The existing exact-limit and unknown-allowance regressions reproduce the fail-closed behavior with zero dispatch. The telemetry's one generation-attempt entry records entering preflight, not a provider call; processed/sample counts and request size were not established. Authoritative limits for the exact deployment are required before another collection-mapping live. Neither the nearby catalog snapshot nor the campaign's 96,000-token ceiling establishes those limits.

Code review stopped during planning. Revisions corrected double field selection, an unsupported encoded-string binding and an attempted synthetic comparison artifact. The last two responses still contained a malformed output-port identifier. The cumulative eight-call/two-repair limits stopped the session. **No Copilot task, command execution, final report or cleanup ran in this cohort.** The producer receipt correction therefore has deterministic transport/recovery evidence, but remains unverified with a live provider on this candidate. All earlier uncertain invocations remain untouched.

The campaign upper bound is **€77.471728023684/€100**: €74.864975239720 verified plus **€2.606752783964 retained unknown reservations**, leaving €22.528271976316. New cohort inference has complete usage receipts. The remaining balance does not reset session limits or justify bypassing missing metadata. [Six-slot report and frozen manifest](evidence/bounded-collection-mappings-2026-10-05/cohort-d.json), [Amazon evidence](evidence/bounded-collection-mappings-2026-10-05/cohort-d-amazon.json), [code evidence](evidence/bounded-collection-mappings-2026-10-05/cohort-d-code.json). Requests, receipts, observations and failed history remain encrypted; public evidence contains sanitized proposals, diagnostics, counts and hashes. Historical cohorts and the historical 33/33 benchmark are unchanged. PR #117 remains draft.

### Reproducible live commands

Use the recorded model/provider policy and the manifest's toolchain environment. `report` is read-only; the retained run IDs cannot be dispatched again. New evaluation requires a new unused cohort/run identity, current readiness checks and explicit review of the generated requirements/task tree.

```sh
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability report --campaign schema-portability-20261002 \
  --workspace "$GNOUGO_WORKSPACE" --cohort collections20261005d
dotnet tests/GnOuGo.Agent.Planning.Benchmark/bin/Debug/net10.0/GnOuGo.Agent.Planning.Benchmark.dll \
  --schema-portability replay-compile --campaign schema-portability-20261002 \
  --workspace "$GNOUGO_WORKSPACE" --run collections20261005b-amazon-1
```

Fresh runs use the same executable with `--schema-portability readiness|plan|execute`, `--case amazon|code`, and new `--cohort`/`--run` values. Execution additionally requires `--review-command <file>` containing the reviewed revision, artifact hash and explicitly selected requirement IDs. Recommendations, acknowledgments and permission responses are never submitted automatically.
