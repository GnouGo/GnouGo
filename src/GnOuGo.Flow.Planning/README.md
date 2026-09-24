# GnOuGo.Flow.Planning

A separately publishable package depending only on Flow.Core.

`Requirements → progressive capability discovery → PlanningGraph → validation → approval`

`HybridWorkflowPlanner` is the only planner. Requirements retain reviewable outcomes and acceptance criteria; `PlanningGraph` is the only executable representation. YAML is compiled deterministically from that graph. Static validation and optional simulations do not establish external execution success.

`ICapabilityCatalog` exposes source summaries, paginated capability summaries and exact versioned contracts. Discovery connects only requested sources. Pages and resolved contracts survive graph repairs. The model retains summaries from every discovered page without reopening them; unrelated unavailable sources become visible limitations. Approval and execution revalidate the selected capabilities, executor contracts and host policy.

The generated language permits literals, typed references, simple conditions and registered typed operations. Built-in numeric and projection operations validate their contracts at runtime. Generated functions and arbitrary computation are rejected. Authored YAML retains its expression language.

Generated `agent.run` stages require a nonempty literal `workspace`, reviewed with the objective, permissions, budgets and verification requirements. Runtime input/output references and interpolation cannot choose this scope. Changing the workspace changes the approval hash; the injected runner still applies its existing path and filesystem policy. Authored YAML retains dynamic workspace inputs.

A failed validation opens one bounded graph revision scope, including affected consumers and subworkflow callers. Other stages and interfaces remain frozen. Invalid scope changes cannot become the next repair baseline. Discovery, revisions and retries share cumulative call, token, cost and elapsed-time ceilings; the defaults remain eight model calls and two graph repairs.

Interactive mode asks typed business clarification questions. Auto mode stops when necessary information is missing. Approval identifies an exact artifact hash covering requirements, graph, selected contracts and budget ceilings. External effects also require the existing host confirmation boundary.

Schema-9 storage rejects previous planning approvals. Old encrypted records are left intact. Regenerate and approve old workflows; there is no legacy planner switch or compatibility execution path. See [migration and implementation status](../../docs/flow-hybrid-v9-implementation.md).

```bash
dotnet build src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -warnaserror
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke -c Release
dotnet pack src/GnOuGo.Flow.Planning -c Release
```

Tests preserve the frozen business requests and independent execution oracles in `tests/Shared/PlanningBenchmarkCases.cs`. The scripted response harness produces graphs directly; no removed intermediate representation is retained for comparison. Live comparisons use an isolated checkout of the parent revision and the shared campaign spending ceiling.
