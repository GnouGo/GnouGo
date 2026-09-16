# Separate executable support from governing applicability

## Outcome

**OFFLINE PASS.** Implemented from `0b027852ac530ee931bfe616fe8d59ed2c511917`, following the approved architecture review of production `5617c1b9024b48f09c0baf49d5e6f146df130879`.

[Machine-readable evidence](planner-governing-applicability.json) records proofs, measurements, source/binary/package hashes and validation logs. No live provider request, LOCAL, MIXED or Stage-1 campaign was run. The LOCAL/MIXED results below are explicitly synthetic, detached offline fixtures.

## First meaningful blocker

The retained `schema5-execution-contributions-diagnostics-1:local` completed admission but failed `DIAGNOSTIC_DESCRIPTIVE_EVIDENCE`. Its descriptive local/in-memory property had a preliminary `external_execute` kind. Qualification exposed no governing target and accepted `excluded / no_requested_execution`. The original answer satisfied its original schema.

The requested execution span `[29:40]` supported the result through its complete request context. Description `[539:599]` requested no new execution, but still needed governing applicability. These are distinct authority questions. The retained output-limit receipt is a separate observation, not evidence about property applicability.

## Root cause

One effect-specific contribution domain controlled executable support and property relevance. It filtered both through preliminary execution kind. Coverage also selected governing targets before realizations were complete, and admission required governing evidence to match target execution facts.

The implementation now qualifies a property without an effect target, realizes effects from support only, and then proves applicability against realized canonical operations. Neither an empty support domain nor singleton target cardinality determines applicability.

## Authority analysis

The pre-patch self-review was recorded before production edits:

1. Failed invariant: executable eligibility and governing applicability need separate proofs.
2. Owner: contribution qualification and canonical admission.
3. Engine-known facts: current source ownership, declaration coverage, realized identities, baseline nodes and occurrence boundaries.
4. Why the invalid choice existed: support and governing used one kind-filtered effect domain.
5. Deterministic elimination: remove property effect selection from qualification/coverage; issue only realized applicability targets afterward.
6. Generality: production uses owned references and stable contracts, without benchmark words or provider branching.
7. Risks: preserve strict execution support, selective/shared properties, necessity conflicts, inactive exact owners and downstream responsibilities.
8. Independence regression: independent effects remain distinct; shared properties require evidence for every selected target.
9. Authority change: one applicability proof owns these property/source-target decisions and downstream consumers reuse it.
10. Recurrence rule: another verified blocker in this authority class requires architecture review before another targeted fix.

Semantic qualification and applicability remain bounded model judgments where typed ownership is insufficient. A proof makes the authority explicit; it does not make semantic answers infallible.

## Implemented action

Inside the existing `intent_operations` sequence:

1. Validate existing source/declaration exclusions and occurrence boundaries.
2. Qualify complete owned evidence as `supports(effect)`, unbound `governing_property`, or evidenced `excluded / no_operation_relevance`; unresolved qualification stops.
3. Resolve support-only realization coverage and public dataflow. Qualified properties and complete clauses remain context, without support, target-selection or retirement authority.
4. Materialize canonical realized operations.
5. Resolve property applicability. Exact current baseline/occurrence ownership can attach deterministically. A plausible singleton still requires a semantic decision. Shared selections contain a target-specific owned-evidence binding for each target.
6. Apply existing revision assessment with attached evidence, record valid exact-owner inactive outcomes, resolve the existing admission-owned dependencies, and commit atomically.

Applicability domains use realized identities, current structural ownership and established scope. Preliminary kind, role, necessity, same-clause membership and descriptive similarity supply no attachment authority. Property assignments project the established target's execution kind without changing the retained preliminary evidence or the target's contract. Their effect mappings add no public dataflow or producer authority.

Exact workflow ownership retains a workflow outcome rather than broadcasting to operations. Valid omission or revision of an exactly owned execution records an inactive outcome linked to its coverage/revision proof. Unknown ownership cannot silently retire or retarget a property. Every property outcome participates in admission fingerprinting, including outcomes with no surviving operation.

Support validation retains its positive bases, execution compatibility, baseline coordinates and occurrence proof. Governing validation reconstructs qualification and applicability separately. Current proofs cover all assignments before commit. Downstream implementation-policy relationships reuse exact covered applicability; permission, confirmation, resource ownership, failure and unrelated relationships keep their existing responsibilities.

The new provider-neutral `PlanningGoverningApplicabilityProof` records contribution/runtime identities, evidence, active target bindings or workflow/inactive outcome, owner references, origin, decision lineage, realized-set/domain fingerprints and a proof fingerprint. It lives on existing admission records; answers remain in existing durable pages. Evaluation caches are temporary derived values, not another authoritative graph or state collection.

| Proof | Version |
|---|---:|
| Execution contribution | 2 |
| Governing applicability | 1 |
| Realization coverage | 3 |
| Effect / admission | 7 / 12 |
| Runtime / source | 7 / 6, unchanged |
| Declaration / occurrence / dependency / baseline projection | 5 / 1 / 1 / 1, unchanged |
| Storage and encrypted namespaces | Schema-5, unchanged |

Canonical operation IDs, necessity, confirmation, declarations, dependency authority, budgets, reasoning, paging, partitioning and singleton escalation are unchanged. Historical effect-bound governing and `no_requested_execution` answers require explicit reassessment; they are not converted into new proofs.

## Validation performed

**Historical evidence:** the retained LOCAL's eleven completed answers still validate against their original schemas; its verified output-limit receipt still has no usable assignment. Before implementation, completed-admission replay made zero calls. Current replay stops at `INTENT_OPERATION_PROOF_MISSING`, with zero provider dispatches. The checkpoint fingerprint remains `cb9662838d30d0ceb91df9dbbbe1f23b6bc0951b93359dab8fac527fe5d24c86`. Original request/receipt fingerprints and archived accounting remain unchanged. The retained MIXED's ten original answers also remain schema-valid; current proof reassessment stops without substituting new receipts.

**Synthetic corrected LOCAL:** current qualification explicitly preserves the description's incorrect preliminary external kind, qualifies one executable support span and three properties (rules, object description, deterministic/local description), and admits exactly required `operation_e226ff9ed819b5a645e7d9de`. Canonical dataflow remains `record + threshold -> classifiedResult`; the operation-producer set is empty. Preservation remains declaration evidence. The three applicability proofs are model-semantic selections, followed by deterministic attachment projections; none is an occurrence-identity decision. Reload validates contribution 2, applicability 1, coverage 3, effect 7 and admission 12 with identical IDs/fingerprints, zero calls and zero writes.

**Synthetic corrected MIXED:** admits required read `operation_6b515f252d71b693829d2fe4` and the same local result operation. The read consumes `sourceId`; the transformation consumes `threshold` and the read result, producing `classifiedResult`. Exactly read → local is established by the existing evidence-backed dependency path. The read's property has one applicability decision; there are no invented operations or identity decisions. Completed re-entry makes zero calls/writes and preserves snapshot/accounting fingerprints. These answers are synthetic, not historical receipts or live convergence.

**Automated gates:** the full solution passed **3,614 tests across 29 assemblies, with one optional provider test skipped**. Planner component: **1,062 passed**. Focused harness: **149 passed**. Focused contribution/applicability/coverage checks: **39 passed**. Solution/harness builds, both frontend builds, four packages, reference selfchecks, Native AOT planning/encrypted restart and trimmed Agent.Server persistence all passed. Final build/install/publish gates emitted no warnings; no suppression was added. The initial concurrent pnpm installs reported a shared-workspace link race, so installation and frontend builds were rerun sequentially and passed without warnings. Existing documented publish-local Jint/framework exceptions remain unchanged.

Regressions cover empty support domains, erroneous external hints, singleton semantic applicability, exact-owner deterministic attachment, mixed subspans, property-only failure, foreign/duplicate targets, shared evidence, explicit necessity conflicts, independent effects, outputless/external/human/resource safety, optional omission, revision inactivity, stale proofs, ordering, durable replay and downstream reuse. Published persistence smokes exercise the added source-generated proof contracts.

## Decision accounting

| Retained LOCAL checkpoint | Historical | Corrected synthetic |
|---|---:|---:|
| Interpretation decisions | 8 | 8 |
| Canonical contribution decisions | 3 | 3 |
| Coverage decisions | 1 | 1 |
| Separate applicability decisions | 0 | 3 |
| Standalone occurrence-identity decisions | 0 | 0 |
| Dependency-model decisions | 0 | 0 |
| Top-level semantic decisions | 12 | 15 |

Two property target selections move out of qualification. The previously excluded description gains an applicability decision. Qualification retains its role assessment. No applicability attachment is claimed deterministic merely because LOCAL has one target.

Final LOCAL packing measures eight interpretation pages, two contribution pages, one coverage page and one applicability page: **twelve nominal initial pages**. The detached admission fixture makes four synthetic requests; actual provider usage is unknown because no provider is called. This is neither a saved-call claim nor a guarantee of fitting truncations/corrections within sixteen calls.

LOCAL contribution schemas are **2,401 / 1,337 / 2,002 bytes**; coverage is **891 bytes**; each applicability schema is **661 bytes**. Synthetic admission request input estimates are **2,549 / 1,670 / 1,558 / 1,846 tokens**.

MIXED measures nine interpretation decisions/pages, three contribution decisions/pages, two coverage decisions on one page, one applicability decision/page, and two dependency decisions on one page. Its six synthetic admission requests estimate **1,935 / 1,680 / 1,802 / 2,115 / 1,108 / 1,624 input tokens**. Its applicability schema is **698 bytes**. Boundary-scope and standalone identity decisions remain zero for these fixtures.

No synthetic admission required partitioning, escalation, semantic repair or an unverifiable dispatch. Detached synthetic reservations are distinguished from archived usage; archived budgets and receipts were not reset or modified. Engine exclusions and attachment projections are not counted as eliminated provider calls. All live token measurements remain unavailable.

## Next gate

**One fresh LOCAL, requiring separate authorization.** Stop here. No live LOCAL, MIXED or Stage-1 success is claimed.
