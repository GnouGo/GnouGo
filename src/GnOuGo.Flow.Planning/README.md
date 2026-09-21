# GnOuGo.Flow.Planning

A separately publishable package depending only on Flow.Core.

`User request → SemanticPlan → complete capability grounding → GroundedPlan → deterministic validation → PlanningGraph → compile → scenarios → approval`

Semantic generation receives business requirements, host instructions and revision context, without capability cards. Grounding evaluates business actions against every authorized catalog page, including calculations or transformations that may require specialized external behavior. Complete viable matches are retained; a bounded selection chooses a sufficient implementation before binding contracts are loaded. Only exact issued identities with recorded semantic matches can be bound; complete coverage is required before a global `none_of_the_above`. Grounding, binding, fixture generation and replanning share the default eight-call allowance. Two replans are allowed by default; configured input, output, usage and time ceilings remain enforced.

The deterministic validator resolves contracts from authoritative capability schemas, literals, supported expressions and explicit validated business outputs. `ValidatedGroundedPlan` is an in-memory receipt over a private snapshot, never a model DTO. `PlanningGraphBuilder.Build` accepts only that receipt and lowers transport wrappers, internal subflows, captures and cleanup guards mechanically.

Missing producer output contracts are explicitly opaque. Whole values can cross scopes, but examples and scenario data cannot authorize field access. A `validate` operation lowers to Core's `value.validate`: explicit JSON-value or JSON-text input, a literal output schema, parsing and runtime validation before projection. Transformations require separately validated output contracts. Neither operation changes a capability contract.

Replanning replaces a complete action or affected business scope atomically, preserves semantic requirements and invalidates downstream artifacts. Unchanged proposals stop early; unexpected host failures stop immediately. There are no holes, finite capability/value choices, ranked shortlists, local JSON patch batches or graph-to-intent reflection.

Schema-8 sessions persist semantic plans, catalog coverage, grounded plans and current phase in new encrypted namespaces. Approval binds those artifacts, the catalog, graph, YAML, fixtures and scenario evidence. Approval and execution reconstruct the graph from a revalidated grounded plan. Existing authorization, current-catalog checks, compiler validation, isolated scenarios, protected-action confirmations and tenant ownership remain mandatory. Old sessions cannot execute through the new pipeline.

Build and test:

```sh
dotnet build src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -p:PublishAot=true
```

The eight-case offline corpus retains independent execution assertions. Historical two-call and repair measurements describe the previous architecture, not this pipeline. See [architecture](../../docs/workflow-planning-v2.md); final live evidence is recorded separately from simulated scenarios.

Oversized binding requests are split at complete semantic subgraphs. Each accepted prefix is persisted and revalidated after restart; subsequent batches consume its established value contracts. Calculation result types are inferred, and unproved whole results stay opaque until `value.validate` establishes a narrower runtime contract. All batches and replans share the configured call allowance.
