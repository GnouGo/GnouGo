# GnOuGo.Flow.Planning

A separately publishable package depending only on Flow.Core.

`Requirements → progressive discovery → LLM TaskPlan → deterministic compilation → PlanningGraph → YAML → validation → scoped TaskPlan repair → approval`

`TaskPlan` is editable business intent. `PlanningGraph` is the sole executable representation. `HybridWorkflowPlanner` uses one bounded discovery/planning/repair loop; resolving selected operations and compiling them do not call a model. Static validation and simulations do not establish external success.

Tasks declare stable IDs, objectives, operation IDs, named business bindings and dependencies. Sequence is the default; concurrency requires a parallel scope or parallel iteration. Conditional alternatives declare matching business outputs. Reusable groups declare their inputs and outputs. Iteration has finite item and concurrency ceilings, preserves order and duplicates, and validates the collection bound before dispatch. Each scope can own `always` tasks.

Task, group and choice declarations share one case-sensitive namespace throughout the TaskPlan, including nested scopes and reusable-group definitions. Their IDs and references use only nonempty ASCII letters, digits, underscores or hyphens, excluding the reserved `__` prefix. Repeated calls do not redeclare a group. Alternative IDs remain local to each choice; business ports, operation IDs and requirement IDs are outside this namespace. Invalid declarations fail closed without normalization, automatic renaming or broader repair permissions. Existing malformed consumer references can be corrected only in their diagnosed binding slots. Recovered plans and approval verification use the same identity rules; regenerate a saved plan with invalid declarations.

`TaskPlanCompiler` owns executor selection, stable generated IDs, request envelopes, references, declared branch merges, collection projections, explicitly supplied literal defaults and cleanup guards. Transient symbol tables and source maps are not another persisted plan. Compilation diagnostics address business tasks and ports. Generated executor validation failures stop as compiler diagnostics; the model is never asked to repair generated plumbing.

`ICapabilityCatalog` exposes source summaries, paginated versioned operation summaries and exact contracts. Ordinary business ports come directly from authoritative schemas. Injected catalogs may supply `PlanningOperation` mappings to exact schema fields; mappings never come from names, descriptions or examples. Registered executors explicitly opt into planning through `StepContract.PlanningEffectKind`. Selected contracts resolve automatically and are revalidated before approval and execution. Discovery pages and resolved receipts survive repair; unavailable unrelated sources remain visible limitations.

`value` copies or assembles data; `transform` interprets named `inputs` using its `objective` and a declared `resultType`. Results must be nonempty closed objects with fully typed required fields, explicit nullable values, and no defaults or opaque types. The compiler emits fixed `template.render` prompt assembly and strict structured `llm.call`, using existing runtime configuration and budgets. The model never supplies templates, model settings or executor envelopes. Transform repairs edit diagnosed bindings or exact type slots without renaming business fields; replacing a `value` task requires explicit revision/regeneration.

Discovery proposals use `discoveryRequests`: one to four issued source/cursor pairs, exclusive with a TaskPlan. The complete batch is validated before sequential metadata reads. Recovery reuses the recorded model response and identity. Superseded pending singular-source requests fail closed with a regeneration instruction and retained accounting. No model-assisted contract-resolution call is added.

Values allow literals, named business references and closed typed predicates. An output reference uses a task ID and business port; a null port selects its whole business result. Opaque results have no typed fields. Presence tests only whether a task produced a non-null result; it proves neither payload shape nor external success. JavaScript, executor types, wire paths, projection recipes and schema pointers are absent from the model response contract. Authored YAML retains the existing runtime language.

Generated agent stages require literal objective, nonempty workspace, capabilities, budgets, output contract and verification requirements. A business choice cannot select these scope fields. The injected runner still enforces paths, permissions, filesystem and inference policy.

`PlanningChoice` binds exactly one business value slot. It declares typed literal alternatives, a recommended ID and a host-owned selected ID. Interactive mode presents alternatives; auto mode validates and records the recommendation without another model call. Selection recompiles locally. Choices never approve execution or replace runtime confirmation.

The model schema restricts alternatives recursively to literals, requires at least two alternatives or parallel branches, and requires nonblank questions/objectives. It expresses item ceilings of 1–10,000, concurrency ceilings of 1–100, predicate arity (one for `not`, two otherwise), and array item types. Identity patterns use explicit ASCII alternatives and ordinary anchors without lookaround or engine-specific escapes. Tests cover JavaScript Unicode regex syntax and .NET; the compiler independently enforces exact lexical checks, including trailing line breaks where regex anchors differ. Semantic validation still enforces identity uniqueness, reference visibility, typed contracts and recommendation membership.

Permanent typed model-provider rejections produce `MODEL_REQUEST_REJECTED` with safe classification/status/code metadata, never a raw provider body. The Agent host blocks unchanged retries of these requests. After correcting the request/configuration, create a new planning session; retained requests and cumulative accounting are not reset. Unknown transport outcomes keep their existing recovery rules.

`MODEL_INPUT_LIMIT` instead stops locally before dispatch when the conservative full-prompt plus response-schema estimate exceeds the saved input allowance. No additional call or repair is consumed. Standalone Flow defaults to 12,000 input tokens per request; new Agent.Server Designer sessions default to 24,000, subject to explicit host overrides. A revision-checked `configure_generation` command can resume a stopped session with no pending request, retaining discovery and cumulative budgets. No automatic limit escalation, context truncation or model retry is performed; later calls still require budget admission.

Compiler preflight validates TaskPlan identities, scopes, business ports and contracts before emitting graph nodes. It collects independent diagnostics, including every invalid cross-scope consumer and required export boundary; unavailable prerequisite contracts suppress cascading errors. Children can capture available ancestor values. Parents consume explicitly declared exports, and parallel siblings cannot directly consume each other. The compiler never invents exports or fallback values.

Repairs edit diagnosed business slots and the explicit export chains needed by their consumers. Conditional counterparts require explicit values. Revalidating dependent tasks does not authorize changing them; unknown locations never expand the scope to the whole plan. Unrelated tasks, interfaces, choices, ordering and compiled stages remain fixed. Rejected repairs retain their immutable baseline. Discovery, retries and revisions share cumulative budgets. Defaults remain eight model calls and two repairs. Approval hashes intent, choices, mappings, exact contracts, graph/YAML and ceilings. Approval verification recompiles and requires the reviewed artifact to match exactly.

Optional workflow and group inputs require literal defaults; optional fields inside an object do not. Independent input errors are reported together, with group-qualified locations. Discovery responses can select only unconsumed source pages; an exhausted catalog requires a TaskPlan response. Composite scope outputs are assembled by typed `set` stages after cleanup, preserving their nested contracts. Repair rejections identify the changed business slots without accepting the rejected proposal as a new baseline. Presence sources remain exact task IDs, not dotted payload addresses.

Planning storage format **10** rejects prior planning sessions and approvals without modifying their encrypted records. Execution journal schema **9** is unchanged. AI revision requires a saved TaskPlan or fresh requirements and renewed approval; YAML import into the planner has been removed.

Consuming an optional operation port emits a checked `value.project` at the consumer. The full authoritative container crosses scope boundaries; the check stays inside the consuming branch, iteration or cleanup. Missing fields fail, nullable values remain nullable, and unused ports need no projection. No fallback, model call or new semantic syntax is introduced.

```bash
dotnet build src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -warnaserror
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke -c Release
dotnet pack src/GnOuGo.Flow.Planning -c Release
```

The eight frozen business requests and independent execution oracles remain in `tests/Shared/PlanningBenchmarkCases.cs`. Small synthetic fixtures keep planner/AOT tests independent. Host product-query tests replay recorded model responses with actual Browser/Document registrations and real XLSX writing; they do not construct a TaskPlan or supply a search URL. See the [contract diagnosis and replay limitations](../../docs/planning-product-contract-diagnosis-2026-09-26.md). Historical cohort evidence remains 8/9, without a new reliability claim. Real Copilot command edit/test execution remains unverified under the available sandbox policy; do not bypass it. See [architecture and migration](../../docs/workflow-planning-v9.md).

Repository agents must use `$gnougo-planning`, the portable [planning skill](../../.agents/skills/gnougo-planning/SKILL.md), as required by the root [AGENTS.md](../../AGENTS.md).
