# GnOuGo.Flow.Planning

A separately publishable, provider-neutral Planner v2 with one deterministic path:
intent and clarification → locked capabilities → business behavior and human review
→ typed graph and dataflow → complete subworkflows → targeted typed repairs
→ deterministic YAML → compilation, scenario and semantic validation → final approval.

`PlanningGraph` is the authoritative executable model. Each model construction request
returns one complete `PlanningWorkflow` JSON object. Callees are validated before callers;
independent workflows run concurrently and commit in stable order. Models never produce
YAML. `PlanningGraphCompiler` owns lowering and capability ownership mappings.

Use `TypedWorkflowPlanner` through Flow.Core's `IWorkflowPlanner.AdvanceAsync` and inject
`IPlanningRuntime`. `WorkflowPlanningRuntime` adapts a Flow engine's model/MCP
clients, telemetry and runtime validators. The integrations package supplies the durable
`WorkflowPlanningRuntimeFactory` for standalone hosts. Every runtime operation carries
explicit ownership or scenario evidence. Hosts own durable, encrypted session storage.

Schema 3 separates intent, construction and validation state. Durable request identities
and receipts support safe recovery. Repairs are atomic, scoped, staged and monotonic;
validation binds the exact graph, contracts and fixtures to the final artifact.
Read-only YAML review follows mandatory business and executable validation.

Defaults: concurrency 4, repairs 3, input ceiling 12,000 tokens per request,
output ceiling 8,192 tokens. Limit exhaustion pauses with diagnostics.

```sh
dotnet build src/GnOuGo.Flow.Planning
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke
```

See [the architecture](../../docs/workflow-planning-v2.md) and
[published smoke](../../tests/GnOuGo.Flow.Planning.Smoke/README.md).
