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
