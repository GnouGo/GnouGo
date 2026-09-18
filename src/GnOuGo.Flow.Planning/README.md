# GnOuGo.Flow.Planning

A separately publishable deterministic workflow planner depending only on Flow.Core.

`PlanningSession → WorkflowIntentPlan → PlanningGraph → Diagnostics → Approval`

Read `TypedWorkflowPlanner.cs` for orchestration, `PlanningGraphBuilder.cs` for catalog resolution and graph construction, and `PlanningGraphCompiler.cs` for YAML lowering. Focused helpers handle catalog discovery, typed hole domains, validation and approval. Natural language is interpreted once; only unresolved choices and bounded repairs need further model calls.

Hosts inject `IPlanningRuntime`. Graph validation checks capability identity, schemas, bindings, availability, dependencies, executable expressions, artifact contracts and explicit policy. A host-owned confirmation gate protects external effects, including subworkflows and cleanup. Final artifact approval is separate from runtime confirmation. Only validated graphs without executable holes compile.

Input references name the current workflow's input port. Capability schema references start at `/input` or `/output` and continue through JSON Schema properties. Finalizers may depend on a main step; the builder guards cleanup so it runs only after that producer completes. Independent diagnostics are reported together, and cycle diagnostics locate the offending step. Pre-dispatch input-budget failures consume neither a model call nor a repair attempt.

`PlanningJsonTransport` supplies the canonical model-facing intent JSON: active value/schema fields, explicit nulls, omitted arguments and holes remain distinct. Arrays require item schemas; objects require typed properties or typed additional properties. Fixtures accept only literal input objects and recursive literal observation responses. Their shape is checked alongside graph errors; absent fixtures still use deterministic sampling.

`PlanningDiagnosticLocations` derives repair coordinates from workflow/step identifiers and named members, accounting for step ordering, MCP arguments and generated confirmation/cleanup guards. Session diagnostics keep graph coordinates. Repair context names the editable intent location (or its existing container and missing field), groups known consequences under the invalid producer schema, and retains independent failures. Defective host-owned fields stop planning without spending a model repair.

Pending model requests retain their original interpretation, repair, or choice phase and reserved response schema on receipt replay. Uncertain dispatches stop. Agent.Server offers an explicit retry that retains cumulative budgets and accounts for missing usage before reserving a new request; the planner never retries them automatically.

```bash
dotnet build src/GnOuGo.Flow.Planning -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
```

See [the architecture](../../docs/workflow-planning-v2.md) for public contracts, defaults, diagnostics, schema-6 persistence, restart and approval semantics. No compatibility adapters or alternate planner remain.
