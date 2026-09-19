# GnOuGo.Flow.Planning

A separately publishable planner depending only on Flow.Core.

`Prompt → business intent → deterministic graph → validation → bounded correction → scenarios → approval`

Start with `TypedWorkflowPlanner.cs`, `PlanningGraphBuilder.cs`, and `PlanningGraphCompiler.cs`. The model describes business operations. The builder owns executable nodes, capability bindings, schemas, result envelopes, internal subflows, ordering, and finalizer guards. The compiler only accepts valid, fully resolved graphs.

Intent contains inputs, operations, outputs, optional named subflows and clarification questions. Its operation variants are `invoke`, `calculate`, `transform`, `choose`, `each`, `parallel`, `call`, and `cleanup`. Values address inputs, operation results and iteration values through business paths. Calculations use sandboxed expressions over named values. Only new business values need type declarations; capability contracts remain catalog-owned. Omitted arguments, missing values, explicit null and defaults remain distinct. Fixtures belong to the session, not the interpretation response.

The graph remains executable authority. Validation checks capability identity, declared schemas, bindings, conditional availability, dependencies, expressions, artifact contracts and host policy. Protected effects require a host-generated runtime confirmation gate, including subflows and cleanup. Human approval of the final artifact is a separate gate. Saving and execution require trusted stored approval and current contracts.

Hosts inject `IPlanningRuntime`. Schema-7 sessions retain cumulative budgets, one durable pending request, completion receipts, scenarios and approval. Earlier formats are rejected. Agent.Server uses new encrypted record namespaces and a new planning database; existing databases are untouched. Reserved requests replay under their original response schemas. Uncertain dispatches stop without automatic resend.

Revision import projects supported saved workflows into business intent and rejects unrepresentable executor configuration explicitly. No raw graph escape hatch is exposed to the model.

```bash
dotnet build src/GnOuGo.Flow.Planning -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
```
