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
