# GnOuGo.Flow.Planning

A separately publishable deterministic workflow planner depending only on Flow.Core.

`PlanningSession → WorkflowIntentPlan → PlanningGraph → Diagnostics → Approval`

Read `TypedWorkflowPlanner.cs` for orchestration, `PlanningGraphBuilder.cs` for catalog resolution and graph construction, and `PlanningGraphCompiler.cs` for YAML lowering. Focused helpers handle catalog discovery, typed hole domains, validation and approval. Natural language is interpreted once; only unresolved choices and bounded repairs need further model calls.

Hosts inject `IPlanningRuntime`. Graph validation checks capability identity, schemas, bindings, availability, dependencies, executable expressions, artifact contracts and explicit policy. A host-owned confirmation gate protects external effects, including subworkflows and cleanup. Final artifact approval is separate from runtime confirmation. Only validated graphs without executable holes compile.

Input references name the current workflow's input port. Capability schema references start at `/input` or `/output` and continue through JSON Schema properties. Finalizers may depend on a main step; the builder guards cleanup so it runs only after that producer completes. Independent diagnostics are reported together, and cycle diagnostics locate the offending step. Pre-dispatch input-budget failures consume neither a model call nor a repair attempt.

```bash
dotnet build src/GnOuGo.Flow.Planning -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
```

See [the architecture](../../docs/workflow-planning-v2.md) for public contracts, defaults, diagnostics, schema-6 persistence, restart and approval semantics. No compatibility adapters or alternate planner remain.
