# GnOuGo.Flow.Planning

A separately publishable package depending only on Flow.Core.

`Requirements → progressive discovery → LLM TaskPlan → deterministic compilation → PlanningGraph → YAML → validation → scoped TaskPlan repair → approval`

`TaskPlan` is editable business intent. `PlanningGraph` is the sole executable representation. `HybridWorkflowPlanner` uses one bounded discovery/planning/repair loop; resolving selected operations and compiling them do not call a model. Static validation and simulations do not establish external success.

Tasks declare stable IDs, objectives, operation IDs, named business bindings and dependencies. Sequence is the default; concurrency requires a parallel scope or parallel iteration. Conditional alternatives declare matching business outputs. Reusable groups declare their inputs and outputs. Iteration has finite item and concurrency ceilings, preserves order and duplicates, and validates the collection bound before dispatch. Each scope can own `always` tasks.

`TaskPlanCompiler` owns executor selection, stable generated IDs, request envelopes, references, branch merges, collection projections, defaults and cleanup guards. Transient symbol tables and source maps are not another persisted plan. Compilation diagnostics address business tasks and ports. Generated executor validation failures stop as compiler diagnostics; the model is never asked to repair generated plumbing.

`ICapabilityCatalog` exposes source summaries, paginated versioned operation summaries and exact contracts. Ordinary business ports come directly from authoritative schemas. Injected catalogs may supply `PlanningOperation` mappings to exact schema fields; mappings never come from names, descriptions or examples. Registered executors explicitly opt into planning through `StepContract.PlanningEffectKind`. Selected contracts resolve automatically and are revalidated before approval and execution. Discovery pages and resolved receipts survive repair; unavailable unrelated sources remain visible limitations.

Values allow literals, named business references and closed typed predicates. An output reference uses a task ID and business port; a null port selects its whole business result. Opaque results have no typed fields. Presence tests only whether a task produced a non-null result; it proves neither payload shape nor external success. JavaScript, executor types, wire paths, projection recipes and schema pointers are absent from the model response contract. Authored YAML retains the existing runtime language.

Generated agent stages require literal objective, nonempty workspace, capabilities, budgets, output contract and verification requirements. A business choice cannot select these scope fields. The injected runner still enforces paths, permissions, filesystem and inference policy.

`PlanningChoice` binds exactly one business value slot. It declares typed literal alternatives, a recommended ID and a host-owned selected ID. Interactive mode presents alternatives; auto mode validates and records the recommendation without another model call. Selection recompiles locally. Choices never approve execution or replace runtime confirmation.

Repairs preserve unaffected tasks, interfaces and compiled stages; discovery, retries and revisions share cumulative budgets. Defaults remain eight model calls and two repairs. Approval hashes intent, choices, mappings, exact contracts, graph/YAML and ceilings. Approval verification recompiles and requires the reviewed artifact to match exactly.

Optional workflow and group inputs require literal defaults; optional fields inside an object do not. Independent input errors are reported together, with group-qualified locations. Discovery responses can select only unconsumed source pages; an exhausted catalog requires a TaskPlan response. Composite scope outputs are assembled by typed `set` stages after cleanup, preserving their nested contracts. Repair rejections identify the changed business slots without accepting the rejected proposal as a new baseline. Presence sources remain exact task IDs, not dotted payload addresses.

Planning storage format **10** rejects prior planning sessions and approvals without modifying their encrypted records. Execution journal schema **9** is unchanged. AI revision requires a saved TaskPlan or fresh requirements and renewed approval; YAML import into the planner has been removed.

```bash
dotnet build src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -warnaserror
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke -c Release
dotnet pack src/GnOuGo.Flow.Planning -c Release
```

The eight frozen business requests and independent execution oracles remain in `tests/Shared/PlanningBenchmarkCases.cs`. Scripted responses now produce TaskPlans. Retained live evidence is unchanged; the authorized candidate evaluates three complex cases, three repetitions each, under the original cumulative campaign ceiling. See [architecture and migration](../../docs/workflow-planning-v9.md).
