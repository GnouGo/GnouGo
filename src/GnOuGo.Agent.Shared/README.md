# GnOuGo.Agent.Shared

Provider-neutral chat and planning API DTOs for Agent.Server and its consumers.
This .NET 10 library has no project dependencies and can be packaged separately.

Planning clients create a session with `PlanningStartDto` and submit
`PlanningCommandDto` with the observed revision. Final approval also carries the
exact artifact hash. `PlanningSessionDto` exposes the semantic `TaskPlan`, compiled
validation findings, review details, budgets and recovery status without exposing
encrypted persistence payloads or model prompts.

`PlanningChoiceDto` carries a stable ID, question, typed alternatives, recommendation
and selected alternative. Interactive clients display the recommendation and submit
choice IDs in `PlanningCommandDto.Selections`. Auto mode selects validated
recommendations locally. Choices target semantic value slots; they cannot grant
permissions, expand budgets or approve execution. Runtime human confirmation remains
separate. Chat requests accept `planningMode`, defaulting to `interactive`.

Planning validation DTOs carry optional validation-stage, rule, computation and
prerequisite context. Compiler diagnostics identify tasks and business ports;
executable details remain available for review. Planning format 10 rejects older
sessions with a regeneration instruction; execution journals retain schema 9.

```sh
dotnet build src/GnOuGo.Agent.Shared/GnOuGo.Agent.Shared.csproj
dotnet pack src/GnOuGo.Agent.Shared/GnOuGo.Agent.Shared.csproj -c Release
dotnet test tests/GnOuGo.Agent.Server.Tests --filter FullyQualifiedName~PlanningConvergenceApiTests
```

See [planner architecture](../../docs/workflow-planning-v9.md) and
[Agent.Server](../GnOuGo.Agent.Server/README.md) for the API lifecycle and hosting.
