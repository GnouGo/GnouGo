---
name: gnougo-planning
description: Develop or review GnOuGo workflow planning, TaskPlan compilation, semantic validation and scoped repair, including their tests and documentation.
---

# Planning

Generate the smallest sufficient executable business TaskPlan for the user's request. Preserve necessary validation, shared computation, cleanup and recovery boundaries; task count alone is not a measure of simplicity.

Never introduce use-case-specific instructions, examples, heuristics, capability names, business entities or expected workflows into this skill, shared planning prompts or production planner logic. Scenario context comes from the user's request, discovered capabilities, authoritative contracts and retained session context. Keep scenario-specific examples and independent expected outcomes in tests and evidence. Do not filter legitimate domain text from dynamic context.

## Responsibilities

Prioritize deterministic validation, precise diagnosis and bounded correction over perfecting the initial prompt. New sessions build one semantic TaskPlan incrementally, from available producers toward consumers: retained requirements and material clarification, capability discovery and exact-contract inspection, then bounded Structured Output extensions. A simple workflow can finish in one draft, followed by its local advisory review. Reuse the existing coordinator, contracts, journal and encrypted session store; add no executable IR or parallel engine.

## Incremental construction

- Keep the accepted TaskPlan authoritative. Construction metadata records the frontier, tentative extension, typed interfaces, decisions, requirement coverage, diagnostics and checkpoint identities; it never grants execution authority. Apply an extension to a clone identified by the accepted checkpoint and hash, and commit it atomically only after deterministic validation and local review.
- Extend host-issued open scopes with small coherent groups of tasks. Validate every concrete contract, binding, reference, type, provenance, assertion, permission and dependency immediately using the compiler's semantic preflight. Future dependencies remain non-executable obligations; never use placeholders or unresolved references. Open containers expose no completion or outputs until their children and compatible exports are closed.
- Each model request receives the retained objective, applicable original requirements and answers, relevant exact contracts, imported interfaces and its current fragment. Retrieve original evidence through bounded identity/version-addressed requests. Do not routinely resend the entire plan, history, requirement directory or catalog, substitute a summary for authoritative intent, or silently truncate indispensable evidence.
- Review each deterministically valid draft with a bounded advisory Judge. Add targeted intermediate reviews at multi-fragment scope closures, joins and captured checkpoint intervals; coalesce overlapping triggers. Verify report schemas, locations and evidence. An invalid or inconclusive report cannot accept a checkpoint. A valid report may retain explicit uncertainty; its opinions are neither deterministic proof nor authorization. Never add a final whole-workflow LLM Judge.
- Reuse typed scoped repair for the current candidate. Earlier changes require dependency-based invalidation, including data, control, captures, exports, groups, cleanup, contracts and accepted decisions. Verify preservation of independent fragments and commitments before rebuilding only the affected region. Preserve invalidated originals as evidence. Stop repeated candidates, non-progress, exhausted allowances or unprovable preservation.
- Persist the exact phase, candidate, context manifest and issued request before dispatch. Resume saved receipts once without regenerating accepted fragments or replaying uncertain calls. Judges, evidence requests and corrections share existing cumulative allowances and journals; admit a draft or correction only when its required review also fits. Capture configurable limits and any explicitly selected large-workflow preset before dispatch; never increase them silently.
- Finalize only with closed scopes and dependencies, explicit requirement statuses and complete deterministic validation of contracts, interfaces, references, effects, provenance, permissions and cleanup. Keep unsupported business claims unverified. Preserve deterministic JavaScript generation through PlanningGraph and compiler v15 YAML lowering, followed by existing artifact approval and execution checks.
- Historical generation and global-review behavior exists only behind captured compatibility versions for safe continuation of issued sessions. New sessions cannot select the monolithic path or disable scoped reviews using a legacy option.

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

Use the smallest diagnostic-derived correction scope under captured policy. Apply edits atomically and revalidate the accumulated plan. Fresh construction may regenerate only the current candidate or its proven invalidation region; historical structural regeneration retains its issued authority. Preserve unrelated work, effects, arguments, ordered assertions, cleanup, accepted interfaces and host-owned bindings. Read-only context grants no edit authority. Stop repeated failures, exhausted allowances and changes whose preservation cannot be established. Explicitly authorized defaults apply only to new sessions; never automatically increase budgets, retries or timeouts to compensate for poor generation.

Scoped business Judges independently compare the retained intent and relevant original evidence with the current fragment after deterministic validation. Their structured observations are advisory, never deterministic proof or correction authority. Use the existing scoped correction and clarification paths; do not infer permissions from judge-proposed edits. Historical optional whole-plan review retains its captured behavior only for compatible historical sessions. Judge calls share the session's existing model-call, token, financial and time allowances, journals and recovery; no parallel allocation or hidden retries.

Before model dispatch, establish compatibility with declared provider/model capabilities and admit the complete serialized request against its effective allowance. Keep input estimates, byte bounds, output reservations and cumulative budgets distinct. Unknown runtime input size is unverified, not proof that a consumer fits. Never truncate indispensable evidence or silently change model settings to obtain admission.

Approval requires explicit requirement acknowledgments, current revision and artifact hash. Recompute contractual coverage; confirmed violations block approval. Do not preselect or manufacture acknowledgments. An explicitly authorized isolated test reviewer must use trusted host-owned authority and artifact-bound evidence, record truthful delegated provenance, and remain unavailable to production approval paths. Individual delegated runtime confirmations must match the exact approved invocation and scope; never grant blanket permission or use Judge opinions as authority. Preserve tenant isolation, encryption, confinement, permissions and cross-process leases. Unknown completion blocks replay and dependent cleanup. Post-execution correction creates a separate unapproved draft.

## Compatibility and validation

Preserve historical TaskPlans, issued schemas, correction authority, receipts, approvals, compiler profiles and YAML. Capture new behavior only for fresh sessions or explicitly authorized revisions; do not reinterpret pending requests or silently migrate historical artifacts.

Reproduce failures with focused regressions and independent business oracles. Check actual emitted requests, exact outputs, effects, cleanup and recovery. Distinguish deterministic fixtures and replay from live-model and execution results. Preserve failed evidence. Paid or externally effectful live evaluations require separate authorization, frozen cohorts and cumulative accounting.

Read the relevant maintained reference when changing its subsystem:

- [Incremental construction](../../../docs/incremental-taskplan-construction.md): checkpoint boundaries, scoped review, capacity and recovery.
- [Architecture and migration](../../../docs/workflow-planning-v9.md): package boundaries, interaction, approvals and historical compatibility.
- [Compiler and correction contracts](../../../src/GnOuGo.Flow.Planning/README.md): schemas, validation and versioned repair authority.
- [Compact compilation](../../../docs/compact-workflow-bindings.md): lowering, ordered checks and compiler v15 compatibility.
- [Runtime mappings](../../../docs/runtime-mappings.md): extraction, sandbox limits, provenance, caching and recovery.
