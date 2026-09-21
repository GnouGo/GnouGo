# Semantic planning and capability grounding

The current architecture replaces the previously frozen deterministic-planner-v2 design.

1. **SemanticPlan:** business actions, outcomes, named inputs and outputs, conditions, collections, parallel branches, reusable subflows and cleanup. No technical capabilities or executable expressions are requested at this stage.
2. **Capability grounding:** complete authorized catalog coverage in bounded pages. Declared behavior and metadata support selection; argument compatibility cannot establish semantic suitability. Each action receives viable matches or an explicit global `none_of_the_above` after all pages complete.
3. **GroundedPlan:** exact capability bindings, technical arguments, explicit computations and adapters, with mappings back to semantic actions and required business outputs. Oversized work is partitioned into balanced complete subgraphs; persisted prefixes are revalidated and provide typed boundaries to later batches.
4. **Deterministic validation:** authoritative contracts, literal defaults, expression parameters, field paths, scopes, availability, cycles and resource dependencies. A nonserializable validation receipt is the only builder input.
5. **PlanningGraph and compile:** mechanical transport and topology lowering, deterministic authorization and compiler checks.
6. **Scenarios and approval:** isolated simulated integration execution, followed by review of a hash-bound artifact. Approval and actual execution remain distinct actions.

Flow.Core owns provider-neutral contracts and runtime primitives without any other GnOuGo dependency. Flow.Planning owns orchestration, grounding, validation and lowering and depends only on Core. Integrations and Server supply models, discovery, encrypted persistence and host policy through interfaces.

Opaque results retain opacity through aliases and enclosing structures. Examples never create contracts. Explicit `value.validate` adapters parse JSON text only when selected and validate whole values before typed extraction. Invalid data fails without creating successful evidence. Adapters and transformations can reference an exact selected capability input/output contract instead of restating it. The host resolves and validates that contract without changing the original source. Business transformations have separate output contracts and cannot stand in for required external observations.

Atomic action/scope replanning replaces local patches and choices. Scope boundaries and semantic requirements remain explicit; each candidate must pass full validation. Repeated unchanged responses and host defects stop early. Every model phase shares cumulative call and usage accounting: eight calls and two replans by default, with existing token and elapsed-time ceilings.

Sessions use schema 8, new encrypted record namespaces and a fresh EF Core SQLite index. Schema-7 evidence remains untouched. Durable reservations, completion receipts, optimistic revisions and uncertain-dispatch recovery remain host-owned. The approval hash covers semantic plans, grounding evidence, grounded plans, catalog, graph, YAML and scenario evidence. Reloaded plans are deterministically revalidated before lowering or approval.

The package [README](../src/GnOuGo.Flow.Planning/README.md) contains build, packaging and test commands. Historical reports retain the observed failures and old performance measurements; they are not evidence of success for this architecture.

Provider strict JSON formatting is requested when the exact business schema supports it. Optional fields retain their original standard JSON Schema; runtime instance validation always enforces the complete contract, regardless of formatting mode.
