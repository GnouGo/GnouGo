# GnOuGo.Flow.Planning

A separately publishable, provider-neutral Planner v2 with one deterministic path:
intent and clarification → locked capabilities → business behavior and human review
→ typed graph and dataflow → unresolved typed fields → targeted typed repairs
→ deterministic YAML → compilation, scenario and semantic validation → final approval.

`PlanningGraph` is the authoritative executable model. The coordinator freezes topology and established contracts, resolves unique bindings,
and asks the model for assignments to remaining field IDs only. One field eligibility
analysis filters types, nullability, availability, obligations and artifact provenance
for both deterministic selection and model schemas. Direct bindings are separate from
computation parameters. Contracts propagate in both directions without rewriting
locked fragments; literals obey destination JSON Schema constraints before dispatch. Callees are validated before callers;
independent workflows run concurrently and commit in stable order. Models never produce
YAML. `PlanningGraphCompiler` owns lowering and capability ownership mappings.

Use `TypedWorkflowPlanner` through Flow.Core's `IWorkflowPlanner.AdvanceAsync` and inject
`IPlanningRuntime`. `WorkflowPlanningRuntime` adapts a Flow engine's model/MCP
clients, telemetry and runtime validators. The integrations package supplies the durable
`WorkflowPlanningRuntimeFactory` for standalone hosts. Every runtime operation carries
explicit ownership or scenario evidence. Hosts own durable, encrypted session storage.

Schema 4 separates intent, construction and validation state. Durable request identities
and receipts support safe recovery. Repairs are atomic, scoped, staged and monotonic;
validation binds the exact graph, contracts and fixtures to the final artifact.
Read-only YAML review follows mandatory business and executable validation.
Progress contracts and shared telemetry report active holes, deterministic resolution,
distinct model exposures, binding/parameter counts and repairs/failures by gate.
Persisted request identities prevent replay from increasing these counts.

Defaults: concurrency 4, repairs per workflow/gate 5, input ceiling 12,000 tokens per request,
output ceiling 8,192 tokens. Limit exhaustion pauses with diagnostics.

Capability selection sizes complete requests, including response schemas, against
the same input ceiling. Each catalog page permits only its declared IDs. Matching
and matching repair use recorded candidates for the affected operation plus declared
prerequisites; completed scopes are checkpointed and revalidated on replay. Unresolved
contracts stop without reopening the unfiltered catalog.

Semantic review uses established graph contracts for local values. Generic native
executor schemas do not override business outputs or grant capability-revision
targets. Reassessment of an invalid review target preserves graph, behavior,
contract and scenario fingerprints, along with consumed repair allowances.

```sh
dotnet build src/GnOuGo.Flow.Planning
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke
```

See [the architecture](../../docs/workflow-planning-v2.md) and
[published smoke](../../tests/GnOuGo.Flow.Planning.Smoke/README.md).
