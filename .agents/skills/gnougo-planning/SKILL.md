---
name: gnougo-planning
description: Develop or review GnOuGo workflow planning, TaskPlan compilation, semantic validation and scoped repair, including their tests and documentation.
---

# Planning

Preserve this architecture:

```text
User request
→ existing planning loop:
    accepted requirements + clarification when necessary
    ↔ bounded MCP discovery and exact-contract inspection
    → LLM business TaskPlan with selected operations
→ deterministic compiler → PlanningGraph → YAML
→ artifact review and approval → execution
```

Requirements, clarification and tool selection are responsibilities inside one bounded loop, not mandatory separate model calls. Clear requests with available contracts can generate a TaskPlan immediately. Accepted requirements remain the review baseline; resolved contracts determine which operations and data bindings are allowed. The removed model-authored technical outcome-proof layer does not return. Compilation validates executable contracts, not arbitrary prose completeness or execution success.

## Business intent and data

- The model selects business operations, inputs, results, dependencies and meaningful conditions. It does not author scripts, executor envelopes, wire paths or technical proof mappings. Add no planning phase, IR or integration-specific planner rules.
- No mapping executor in TaskPlan: **typed → JavaScript compiled at final lowering; insufficiently typed → DynamicMappingExecutor at runtime**. Use `value`/`field` for copies and assembly; `extract` for observed JSON, text or HTML; reserve `interpret` for decisions, comparisons and synthesis. Omitted transform mode retains historical interpretation.
- Fresh extraction bindings infer independent collection processing only for an unambiguous source/target pair. Use existing `each: { input, output }` to select explicitly when needed. Exactly one result per item, same order, duplicates and nesting. No implicit filtering, flattening, ranking or aggregation.
- Bounded complete examples can generate a mapping; the script must process and validate every original item. Runtime independent extraction exposes canonical `item` and approved shared `context`; technical input paths do not belong in TaskPlan. Fresh adaptive bindings try a generic mapping, then specialize only unresolved shape/cause groups under the existing explicit cumulative runtime budget. All items still execute and validate; no partial publication. Representative complete examples are optional beyond one fitting initial item; specialization must include the actual failing item. Historical bindings retain one generation and one repair. Cache only validated source-grounded mappings per tenant. Missing required observations return `CONTRACT_UNSATISFIED`, never invented business values.
- Give each consumer its own closed extraction `resultType` and explicit selected input fields. Emit only observed facts needed for that decision; keep full observations and exact later action references separately. Optional candidates are an array per complete source item, empty only after checked absence. Explicit `flatten` with one operand concatenates one typed, nonnullable array-of-arrays level; it preserves ordering, duplicates, nullable elements and deeper nesting. No implicit flatten/filter/grouping. Global interpretation receives the selected view, never sampled/truncated. Validate contracts, closed assembled inputs and the complete serialized request size; do not infer relevance or completeness from shape. Original producer metadata still establishes completeness. Oversized necessary views fail before dispatch.
- For selection, extract only identity plus the decision fields declared by `resultType`; the compiler never chooses relevance. Keep authoritative action arguments separately. Prefer producer IDs, otherwise attach original collection indices before extraction; no content hashes or renumbering. Interpret selects IDs; `lookup(port: key, items: [records, ids])` reconnects offered candidates, then original records. Repeated selections preserve order and repeat records; unknown or ambiguous source IDs fail atomically. Lookup is reconnection only, not a join/filter framework or learned mapping helper. Producer action checks still apply.
- Defaults require authoritative declarations or accepted public-input defaults; absence differs from explicit null. Never create caller inputs to hide missing producer data.
- Never generate a numeric executor. Ordinary formulas compile to JavaScript Number expressions; exact business arithmetic belongs to the owning MCP. Validate declared decimal conversions automatically at MCP boundaries.

## Contracts, scopes and compilation

- MCP producers own argument/result schemas, capabilities, effects, permissions and artifact provenance. Preserve opaque results; matching names or examples do not establish typed fields or resource ownership. No tool-name, provider-name or site-specific planner rules.
- When a producer offers observed action references, select from those candidates and let that producer resolve identity and validate action compatibility. Keep declared action intent explicit. Never synthesize references or treat labels as proof of control semantics; matching an action type does not prove the business decision was correct.
- Validate contract closure before final lowering: types, constraints, nullability, availability and authoritative provenance. Known incompatible values cannot escape through dynamic mapping. Compiler plumbing failures are compiler failures, not model repair requests.
- Keep normal exports in the normal execution path after their producers. Direct validated references stay direct; fuse compatible checked properties without losing intermediate constraints or observable identities. Reserve `finally` for genuine preservation/cleanup and exports that depend on its results; never spend cleanup capacity transporting normal outputs.
- Keep mapping glue compact. Fuse safe typed selections and copy-only loops; preserve real operation loops, guards, cleanup and captures. New compilation profiles belong to existing approval-fingerprinted options. Never reinterpret historical YAML.
- Step `description` is optional literal observability metadata: business objective for business steps, concise deterministic purpose for technical steps. Keep `InternalRole` machine-readable. Never interpret descriptions or use them for execution, inference, permissions, validation or reconciliation; changing YAML metadata still invalidates artifact approval.
- `dependsOn` uses eligible same-scope identities; data references already imply dependencies. Capture ancestors, export descendant values through each scope, and reference the enclosing task's port. Conditional branches explicitly project the same consumer-facing ports with matching types, nullability and requiredness from their own observed values. Prefer direct typed ports over forwarding differently shaped whole results; valid object exports and opaque pass-through remain supported. Keep nullable ports nullable and use explicit null predicates on the consuming execution path—a separate availability boolean does not establish non-nullability. Never invent branch values or flatten/cast opaque contracts. Interface restructuring outside issued repair slots requires explicit revision. `present(inner)` cannot reference an inaccessible descendant or be silently replaced with `present(container)`.
- Keep fixed agent scope and host-owned bindings immutable. Save available failure evidence inside the appropriate finalizer before nested cleanup. Unknown external completion blocks both cleanup and replay.
- Preserve declared constraints in authoritative validation. Project only provider-incompatible schema features out of a cloned model-facing schema before estimation, hashing and persistence. Preflight before dispatch; retain issued requests unchanged.

## Interaction, repair and approval

- Ask only material business ambiguities in the existing planning loop. Questions support recommended alternatives and custom text, pause automatic mode and grant no runtime permission.
- Review accepted requirements beside actual operations, loop bodies and bindings. Artifact approval requires explicit `ReviewedRequirementIds`, current revision and artifact hash. Never populate acknowledgments automatically; compilation is not proof of business completeness.
- New hosts omit redundant startup confirmation; explicit policy opt-in, historical confirmation workflows, MCP permissions, business questions and observed cookie handling remain effective.
- Prefer targeted `revise` with `preserveRequirements: true`, current revision/hash and optional `editablePaths` for implementation-only changes. Preserve unrelated operations, objectives, ordering, scopes and cleanup. Structural redesign requires an explicit global revision.
- Repairs use only diagnostic-derived typed slots, applied atomically to a clone and fully revalidated. Broken producer references may select compatible ports or authorized export chains, never replacement literals. Require explicit revision when immutable guards or missing producers cannot be repaired within issued authority.
- Preserve issued repair envelopes, receipts, cumulative budgets, historical approvals and tenant ownership. Revisions invalidate current artifacts/approval. Designer advancement needs the existing cross-process lease; unknown completion remains stopped, never a free retry.

## Validation and references

Reproduce failures deterministically with independent execution oracles. Check exact output values, effects, cleanup and recovery—not compilation alone. Preserve all failed evidence. Paid evaluations require authorization, fresh identities, frozen cohorts and shared cumulative accounting; never replay uncertain invocations.

Read [architecture and migration](../../../docs/workflow-planning-v9.md) and [compiler/repair contracts](../../../src/GnOuGo.Flow.Planning/README.md) for implementation details. Read [runtime mapping](../../../docs/runtime-mappings.md) for extraction/cache limits and [compact compilation](../../../docs/compact-workflow-bindings.md) for the current lowering profile.
