# Semantic planning and capability grounding

The current architecture replaces the previously frozen deterministic-planner-v2 design.

MCP capabilities come from configured servers through the normal discovery and
execution path. Agent.Server supplies no virtual review publisher or GitHub-specific
catalog filter. GitHub mutations use the configured official GitHub MCP, with the
same generic workflow approval and runtime confirmation boundaries as other
effects. See [MCP workflow execution and migration](github-mcp-workflow-execution.md).

1. **SemanticPlan:** business actions, outcomes, named inputs and outputs, conditions, collections, parallel branches, reusable subflows and cleanup. No technical capabilities or executable expressions are requested at this stage.
2. **Capability grounding:** complete authorized catalog coverage in bounded pages. Declared behavior and metadata support selection; argument compatibility cannot establish semantic suitability. Each action receives viable matches or an explicit global `none_of_the_above` after all pages complete.
3. **GroundedPlan:** exact capability bindings, technical arguments, explicit computations and adapters, with mappings back to semantic actions and required business outputs. Oversized work is partitioned into balanced complete subgraphs; persisted prefixes are revalidated and provide typed boundaries to later batches.
4. **Deterministic validation:** authoritative contracts, invocation fallbacks, literal defaults, expression parameters, field paths, scopes, availability, cycles and resource dependencies. A nonserializable validation receipt is the only builder input.
5. **PlanningGraph and compile:** mechanical transport and topology lowering, deterministic authorization and compiler checks. Grounded and graph contracts use the same conservative inclusion proof; disagreements after validated lowering stop as host defects without consuming model replans.
6. **Scenarios and approval:** isolated simulated integration execution, followed by review of a hash-bound artifact. Approval and actual execution remain distinct actions.

Flow.Core owns provider-neutral contracts and runtime primitives without any other GnOuGo dependency. Flow.Planning owns orchestration, grounding, validation and lowering and depends only on Core. Integrations and Server supply models, discovery, encrypted persistence and host policy through interfaces.

Opaque results retain opacity through aliases and enclosing structures. Examples never create contracts. Explicit `value.validate` adapters parse JSON text only when selected and validate whole values before typed extraction. Invalid data fails without creating successful evidence. Adapters and transformations can reference an exact selected capability input/output contract instead of restating it. The host resolves and validates that contract without changing the original source. Business transformations have separate output contracts and cannot stand in for required external observations.

Atomic action/scope replanning replaces local patches. Scope boundaries and semantic requirements remain explicit; each candidate must pass full validation. Repeated unchanged responses and host defects stop early. Every model phase shares cumulative call and usage accounting: eight calls and two replans by default, with existing token and elapsed-time ceilings.

Sessions use schema 8, new encrypted record namespaces and a fresh EF Core SQLite index. Schema-7 evidence remains untouched. Durable reservations, completion receipts, optimistic revisions and uncertain-dispatch recovery remain host-owned. The approval hash covers semantic plans, grounding evidence, grounded plans, catalog, graph, YAML and scenario evidence. Reloaded plans are deterministically revalidated before lowering or approval.

The package [README](../src/GnOuGo.Flow.Planning/README.md) contains build, packaging and test commands. Historical reports retain the observed failures and old performance measurements; they are not evidence of success for this architecture.

Provider strict JSON formatting is requested when the exact business schema supports it. Optional fields retain their original standard JSON Schema; runtime instance validation always enforces the complete contract, regardless of formatting mode.

Catalog rows retain full producer descriptions and metadata. Binding views factor repeated schemas with local references and export named business results; intermediate implementation results remain private. Later batches have schema-enforced empty input/subflow declarations. These transport reductions preserve catalog coverage and every schema assertion.

Atomic semantic replacements preserve complete output declarations and reuse coverage only for unchanged action definitions against the identical catalog. Bound replanning expands through captured producers, dependent consumers and named-subflow callers before selecting the enclosing scope. Unrelated subflows remain immutable.

Core validates successful cleanup outputs using proven producer-availability guards. Scenarios make those guards false by failing each actual producer and verifying skipped cleanup. They do not manufacture a successful acquisition with an impossible false availability guard. Arbitrary conditions still receive both branch tests. Host compiler disagreements stop before fixture generation or model replanning.

Binding preserves semantic parameter descriptions and selected capability metadata. Selection includes declared artifact prerequisites. A binding response may report located missing prerequisites without any executable operations; the coordinator then replans the semantic action/subgraph atomically. Cleanup guards include earlier finalizers, and successful availability proofs follow the complete dependency chain.

Scenario failures retain their full evidence. Replanning omits a propagated workflow-call failure only when the same failed scenario identifies a failure inside that call’s declared callee; the business cause remains blocking. A wrapper failure without that evidence remains a host defect.

## Planner decisions

Technical computation failures follow bounded repair, independently of business decisions. Core publishes the statically supported computation profile consumed by binding and replanning prompts. Direct scalar `String` and URI encode/decode calls retain their successful string type; unknown contracts still require explicit validation before projection. Optional diagnostic context records expression provenance, known contracts and root producer locations so consumers blocked by one computation do not appear to require separate business choices. Existing schema-8 diagnostic serialization is unchanged when context is absent.

One provider-neutral decision contract adds Auto and Interactive modes without changing this pipeline. New sessions default to Interactive. Validated business alternatives can pause semantic planning, grounding selection after full coverage, binding, or scoped replanning. A durable continuation retains the operation, action/subgraph scope, candidate results and semantic/catalog fingerprint. Option answers apply saved results; custom answers are scoped model input and must pass the same deterministic validators.

Auto checkpoints the preferred selection and reason before continuing. Interactive checkpoints `waiting_for_decision` and requires an explicit revision-checked answer. A mode change to Auto resolves a pending decision; cancellation retains the journal. Technical repairs, deterministic choices, host defects, permissions and executor plumbing do not request decisions. Runtime `human.input` and FinalReview approval remain independent.

Server uses one shared card for Designer and Chat, with encrypted origin links and workflow-owner leases. A restarted chat resumes only its saved planner, through FinalReview; workflow-owned sessions are inspection-only in Designer. [Contract details and local acceptance evidence](planner-decisions-2026-09-23.md) describe the API and recovery boundaries.

## Scoped prerequisite repairs and business scope changes

Binding can report optional structured prerequisites with declared consumer/contract references and root/dependent action links. Missing observations and artifact producers are technical repairs. Replanning receives the accepted scope, complete coverage findings and selected contracts, inserts required producers, and leaves unrelated semantic actions byte-for-byte unchanged. Newly introduced or changed actions still cover the entire authorized catalog. Opaque responses remain opaque until runtime validation succeeds.

A repair checkpoint stores a proposed replacement separately from the active semantic plan, grounding and binding prefix. Fingerprint, boundary, reusable-work and lower-bound budget checks precede atomic installation. Restart reuses the checkpoint and model receipts without duplicate proposals or reset budgets. Infeasible proposals stay visible as unapplied repairs. Existing schema-8 payloads and reserved model schemas remain readable.

Ordinary decisions choose among valid implementations of agreed requirements. An unsupported required outcome instead needs a business revision: Interactive shows the exact scoped change and requires explicit acceptance, rejection or cancellation; Auto stops. No preferred option can silently reduce a mandatory outcome. The separate planner provider carries these answers in the originating Chat, including after restart, while `/planning` remains read-only for workflow-owned sessions.

See [implementation verification](planner-prerequisite-repair-2026-09-23.md) for the reported incident, tests, live results and budget limitations.
