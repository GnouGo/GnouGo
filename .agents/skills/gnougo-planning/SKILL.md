---
name: gnougo-planning
description: Develop or review GnOuGo workflow planning, TaskPlan compilation, semantic validation and scoped repair, including their tests and documentation.
---

# Planning

Generate the smallest sufficient executable business TaskPlan for the user's request. Preserve necessary validation, shared computation, cleanup and recovery boundaries; task count alone is not a measure of simplicity.

Never introduce use-case-specific instructions, examples, heuristics, capability names, business entities or expected workflows into this skill, shared planning prompts or production planner logic. Scenario context comes from the user's request, discovered capabilities, authoritative contracts and retained session context. Keep scenario-specific examples and independent expected outcomes in tests and evidence. Do not filter legitimate domain text from dynamic context.

## Responsibilities

Use the existing bounded planning loop: requirements and material clarification, capability discovery and exact-contract inspection, then a structured semantic TaskPlan. Clear requests with available contracts can complete in one model call. Add no IR, executor, mandatory planning phase or model judge.

- The LLM selects business operations, arguments, decisions, conditions, dependencies and requested results. It identifies semantic sources and fields using existing TaskPlan bindings. It does not author executor envelopes, wire paths, scripts or technical proof mappings.
- The deterministic compiler resolves declared bindings, performs typed copying, field projection and object/row assembly, derives technical dependencies, validates contracts and lowers through PlanningGraph to YAML. YAML is never the model's target representation.
- Compilation must not invent business decisions, defaults, missing values or external effects. Compatible shapes and similar names do not establish meaning, identity or producer ownership.
- MCP producers own operation contracts, effects, permissions and provenance. Discover unavailable contracts; do not simulate external work with transforms or replace execution with an unrequested document.

## Composition and contracts

Prefer direct consumer bindings and scope exports for deterministic values. Keep an explicit task only when it performs business work or preserves a necessary computation, validation or durability boundary. Do not replace interpretation with copying merely because their types match.

- `requires` is a mandatory Boolean assertion: false fails before work. `conditional` routes alternative execution paths. Preserve short-circuit order and nullable access checks.
- `extract` returns observed facts from supplied evidence; `interpret` performs genuine decisions or requested synthesis. Neither performs tool calls. Historical omitted transform mode retains its meaning.
- `value` is deterministic typed assembly. `field` selects one literal declared object property. `lookup` reconnects records by identity; it does not merge objects. `flatten` concatenates one explicit collection level. `foreach` expresses repeated work, including a necessary typed collection projection.
- Preserve exact requested content, opaque identities, order, repetitions and explicit nulls. Missing differs from null; defaults require authoritative declarations. Never fabricate data to satisfy a result contract.
- Preserve complete required observations and producer provenance. Narrow decision inputs through explicit declared fields; do not silently sample, truncate, filter or deduplicate. Bounds do not increase host limits or prove completeness.
- Validate types, constraints, nullability, scope, availability and provenance before lowering. Keep opaque contracts opaque. Preserve complete schema alternatives and runtime validation; compiler defects are not model repair requests.
- Data references imply dependencies. Ancestor captures and explicit scope exports preserve availability; conditional branches expose matching interfaces. Keep normal results in the normal path and genuine preservation/cleanup in finalizers.

## Intent, correction and approval

Compare accepted requirements and actual work with the original request and retained answers. Commitments are non-executable obligations grounded in inspected operations and declared producer/public-output ports. Never derive missing obligations from the plan being assessed.

Keep verified clauses, violations and unverified aspects distinct. Operation presence, structured output and compilation do not prove arguments, prose completeness, per-item execution or successful effects. Human acknowledgment does not upgrade uncertainty into verification.

Clarify only material missing business decisions or facts through the existing interaction loop. Preserve recommended alternatives, custom answers and Auto/Human behavior; recommendations are not authorization. Answers preserve accepted intent, correction authority and cumulative accounting. Requirement/interface changes require explicit revision.

Use the smallest diagnostic-derived correction scope under captured policy. Apply edits atomically and revalidate the complete plan. Preserve unrelated work, effects, arguments, ordered assertions, cleanup, accepted interfaces and host-owned bindings. Read-only context grants no edit authority. Stop repeated failures, exhausted allowances and changes whose preservation cannot be established. Never increase budgets, retries or timeouts to compensate for poor generation.

Approval requires explicit requirement acknowledgments, current revision and artifact hash. Recompute contractual coverage; confirmed violations block approval. Do not preselect or manufacture acknowledgments. Preserve tenant isolation, encryption, confinement, permissions and cross-process leases. Unknown completion blocks replay and dependent cleanup. Post-execution correction creates a separate unapproved draft.

## Compatibility and validation

Preserve historical TaskPlans, issued schemas, correction authority, receipts, approvals, compiler profiles and YAML. Capture new behavior only for fresh sessions or explicitly authorized revisions; do not reinterpret pending requests or silently migrate historical artifacts.

Reproduce failures with focused regressions and independent business oracles. Check actual emitted requests, exact outputs, effects, cleanup and recovery. Distinguish deterministic fixtures and replay from live-model and execution results. Preserve failed evidence. Paid or externally effectful live evaluations require separate authorization, frozen cohorts and cumulative accounting.

Read the relevant maintained reference when changing its subsystem:

- [Architecture and migration](../../../docs/workflow-planning-v9.md): package boundaries, interaction, approvals and historical compatibility.
- [Compiler and correction contracts](../../../src/GnOuGo.Flow.Planning/README.md): schemas, validation and versioned repair authority.
- [Compact compilation](../../../docs/compact-workflow-bindings.md): lowering, ordered checks and compiler v15 compatibility.
- [Runtime mappings](../../../docs/runtime-mappings.md): extraction, sandbox limits, provenance, caching and recovery.
