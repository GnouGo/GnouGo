---
name: gnougo-planning
description: Develop or review GnOuGo TaskPlan planning, deterministic compilation, semantic validation and scoped repair. Use for planner changes and regression diagnosis, not unrelated runtime or persistence work.
---

# GnOuGo planning

## Architecture and ownership

- Preserve Requirements → Discovery → LLM TaskPlan → deterministic compiler → PlanningGraph → YAML → validation → approval.
- TaskPlan holds explicit semantic intent. PlanningGraph is the sole executable representation. Semantic preflight runs inside compilation, before lowering; symbols and source maps are transient bookkeeping.
- Do not add a planning IR, binding session, model phase or parallel planner implementation. Flow.Core owns provider-neutral contracts and must not depend on another GnOuGo package. Integrations own provider-specific mappings and transports.
- Planning storage is format 10; execution journals are schema 9. Preserve runtime, tenant isolation, encrypted persistence and recovery boundaries unless the task explicitly changes them.

## Model and deterministic responsibilities

- The model may select declared operations and generate tasks, dependencies, business inputs/outputs, structured scopes, literal values, named references, typed predicates and typed business choices.
- The model must explicitly declare branch exports and alternative values. It must not generate executor types, wire paths, schema pointers, projection recipes, JavaScript or unverified contracts.
- Derive bindings only from authoritative schemas or validated injected metadata. Never infer semantics from provider/tool names, descriptions, examples or benchmark names.
- Validate identities, scope visibility, dependencies and business contracts before lowering. Collect independent errors at task/port locations; suppress dependent errors whose prerequisite contract is unavailable.
- Task, group and choice declarations share one case-sensitive namespace across the entire TaskPlan. IDs and their references contain only nonempty ASCII letters, digits, underscores or hyphens and must not start with `__`. Alternative IDs remain local to their choice; business ports and catalog operation IDs are not part of this namespace. Never normalize or rename invalid declarations automatically.
- Keep the model JSON schema aligned with existing semantic rules, including recursive literal choice alternatives, predicate arity and bounded iteration/concurrency. Retain deterministic semantic checks for recovered and programmatically constructed plans.
- The compiler owns stable generated IDs, executor envelopes, projections, declared branch merges, bounded collection mechanics and cleanup guards. Apply only declared literal defaults; never invent exports, fallback values or requiredness.
- Retain shared graph/runtime validators. Invalid executor plumbing after successful semantic validation is a compiler defect; do not ask the model to repair it.

## Scoped repair and safety

- Derive minimal complete edit permissions from the immutable baseline and diagnostics. Permit related export declarations and consumer bindings together. Revalidation of a dependent task does not grant permission to edit it.
- Preserve unrelated tasks, interfaces, ordering, choices, scopes and ceilings. Added exports must connect diagnosed producers to affected consumers; reject unrelated additions and rewrites atomically.
- Never widen repair to every task when a location is unknown or ambiguous. Stop safely when no bounded repair can be identified. Rejected proposals must not replace the baseline or reset discovery, receipts, request identities or budgets.
- Invalid or colliding declarations grant no repair permissions. A malformed reference can only be corrected in its already diagnosed consumer binding; it never authorizes identity rewrites or wider edits. Check candidate identities before adopting a proposal or copying selections, and enforce the same rules after recovery.
- Keep opaque values opaque. Presence proves neither payload shape nor external success. Static checks, simulations and assistant claims are not observed execution evidence.
- Keep agent workspace, objective, permissions, budgets and verification requirements literal and approved. Choices cannot grant permissions, raise ceilings or replace runtime confirmation.
- Recompile for approval verification. Changed intent, choices, mappings, contracts or generated artifacts invalidate approval. Never bypass filesystem, sandbox or permission enforcement to make a test pass.

## Development method

1. Preserve original evidence. Reproduce the failure with sanitized deterministic fixtures and independent behavioral assertions.
2. Demonstrate the failing regression, make the smallest generic fix, and run affected tests plus required deterministic/CI checks. Keep package boundaries and warning-free builds.
3. Never weaken an oracle, relax safety checks, special-case a corpus name or hide failed/inconclusive outcomes.
4. Do not initiate paid/live evaluation as an ordinary development check. Never iterate paid/live benchmarks to tune toward a passing result. Any separately requested evaluation must freeze code, corpus, oracles, limits and stopping rules before dispatch; retain every outcome and reservation.
5. Update current guidance, delete clearly superseded code/instructions, and preserve historical evidence. Report unverified Copilot sandbox execution as a limitation rather than bypassing it. Stop when the authorized deterministic work is complete.

Read [planning architecture and migration](../../../docs/workflow-planning-v9.md) for host/approval boundaries and [package checks](../../../src/GnOuGo.Flow.Planning/README.md) for build and test commands. Historical evaluation reports are evidence, not authorization to launch another campaign.
