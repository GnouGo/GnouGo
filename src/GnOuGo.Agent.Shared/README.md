# GnOuGo.Agent.Shared

Provider-neutral chat and planning API DTOs for Agent.Server and its consumers.
This .NET 10 library has no project dependencies and can be packaged separately.

Planning clients create a session with `PlanningStartDto` and submit
`PlanningCommandDto` with the observed revision. Final approval also carries the
exact artifact hash. `PlanningSessionDto` exposes human-review state, diagnostics,
budgets and workflow progress without exposing persistence payloads or model prompts.
Typed outcomes distinguish approved workflows, business clarification and proven
unsupportedness. Technical stops carry a separate reason. Decision-page counts,
input targets and the routine/behavior/semantic reasoning profile use the existing
designer controls; `FinalReview` remains a waiting state.
`PlanningClarificationDto` also carries the business question, canonical choice IDs,
labels, preference reasons, evidence references and dependency fingerprint. Clients
display labels, leave every option unselected, and submit the chosen ID in the
existing answer envelope. Custom text is validated in the same session.

`PlanningWorkflowDto` reports active holes, deterministic resolutions, distinct
model-exposed holes and repeated request exposures. `HoleChoices` separates direct
binding counts from computation-parameter counts; unknown historical attribution
is nullable. Workflow and session gate progress retains repair and failure counts
across revisions and restart. There is no generic retry command. These fields support
the existing designer layout.

```sh
dotnet build src/GnOuGo.Agent.Shared/GnOuGo.Agent.Shared.csproj
dotnet pack src/GnOuGo.Agent.Shared/GnOuGo.Agent.Shared.csproj -c Release
dotnet test tests/GnOuGo.Agent.Server.Tests --filter FullyQualifiedName~PlanningConvergenceApiTests
```

See [planner architecture](../../docs/workflow-planning-v2.md) and
[Agent.Server](../GnOuGo.Agent.Server/README.md) for the API lifecycle and hosting.
