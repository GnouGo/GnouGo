---
name: gnougo-planning
description: Develop or review GnOuGo workflow planning, TaskPlan compilation, semantic validation and scoped repair, including their tests and documentation.
---

# Planning

Requirements → Discovery → LLM TaskPlan → deterministic compiler → PlanningGraph → YAML → validation → approval.

## Contracts and compilation

- TaskPlan is explicit semantic intent; PlanningGraph is the sole executable representation. Add no intermediate representation, model phase or parallel planner.
- No MCP server/tool-specific logic in Flow.Planning or Flow.Core: never branch on names, catalog IDs, URLs, domain terms or prompt keywords. Producer behavior and provider mappings belong in producer metadata or injected integrations.
- Generic metadata ranking is permitted only for discovery presentation: rank retained candidates across pages using request/requirements plus source-scoped refinements, pack optional contracts within 90% of the saved input allowance, retain query/cursor expansion and complete receipts; explicit discovery inspection selections keep exact contracts visible until replaced and grant no execution permission; never infer behavior, types or permissions from relevance.
- Catalog-owned fixed/request bindings stay host-owned: omit them from editable ports, reject explicit overrides, and permit only diagnosed removal in repair. Preserve full contracts and validate the effective request.
- Bind through authoritative JSON Schema and validated provider-neutral metadata. Expose declared finite domains in compact indexes; constrain resolved scalar literals in response schemas and validate references semantically. Prefer generic contract fixes over prompt exceptions or inferred behavior.
- The model selects operations, business bindings, bounded structured scopes and choices with literal alternatives. It must not generate executor envelopes, wire paths, JavaScript, projection recipes or invented contracts.
- Request the smallest sufficient plan with explicit objectives. Compact only representation defaults; never drop business intent or weaken contracts. Accepted requirements remain host-owned. `value` assembles, `field` selects declared fields, `json` serializes; `transform` interprets data with an explicit closed result type, never simple wiring.
- Validate identities, scope visibility, dependencies and types before lowering; collect independent task/port diagnostics. Keep response schemas aligned with semantic checks. The compiler owns stable IDs, envelopes, projections and guards.
- Require explicit scope exports, defaults and branch values; never invent them. Reuse declared resource locations for creation and cleanup, including partial failure.
- Preserve optionality, nullability, enums, opaque contracts and availability checks. Fail closed on ambiguity or unavailable contracts. Envelope presence proves neither payload non-nullability nor external success.
- Artifact origin comes only from declared producer metadata; preserve it through checked identity-preserving bindings. Types, literals and transforms cannot manufacture provenance. Present artifact kinds and business ports in compact discovery, never wire pointers; find missing prerequisites with bounded exact-kind metadata queries.

## Repair and approval

- Derive minimal complete edit permissions from the immutable baseline and diagnostics. A diagnosed optional operation binding may be explicitly omitted only under its authoritative contract; null is not omission. Expose only incompatible producer constraint leaves. Dependent revalidation does not grant edits; unknown locations never widen scope.
- Initial generation returns TaskPlan; repairs return typed host-issued patches only. Reuse revision permissions, apply to a clone and fully revalidate. Send only affected context; never insert/delete/reorder tasks or regenerate the baseline. Bind recovery to the original baseline, scope, contracts and request schema; never silently rebase.
- Preserve unrelated tasks, interfaces, ordering and choices. Reject unauthorized proposals atomically; retain discovery receipts, request identities and cumulative budgets through recovery. Validate recovered responses against their saved request schemas.
- Reserve one proposal and one repair when allowed; additional repairs are opportunistic within saved ceilings. Successful discovery clears only resolved response errors, never history or counters. Enforce issued schemas and stop safely; token settings cannot replenish calls.
- If resolved plan operations lack a required artifact producer, stop for explicit semantic revision; binding repairs cannot insert tasks. Unavailable contracts are uncertainty, not proof of absence.
- Invalid generated plumbing is a compiler defect, not a model repair. Do not remove guards or relax validation to accept it.
- Keep agent workspace, objective, permissions, budgets and verification requirements literal and approved. Choices grant no permissions. Recompilation must reproduce the reviewed artifact; changed intent, selections, contracts or artifacts invalidate approval.

## Development

- Preserve failures; reproduce with sanitized fixtures and independent oracles. Add a deterministic regression, make the smallest generic correction, and run affected tests and CI checks. Never weaken an oracle.
- No paid/live evaluation without explicit authorization. Never iterate paid/live benchmarks toward a passing result; retain failed and inconclusive evidence. Simulations and assistant claims are not external execution evidence.

Read [architecture and migration](../../../docs/workflow-planning-v9.md) for storage/approval boundaries and [planner contracts and checks](../../../src/GnOuGo.Flow.Planning/README.md) for details.
