# Explicit request/qualifier composition — offline implementation

## 1. Outcome

Implemented nested governing qualifiers inside the existing indivisible clause-qualification decision. No planning phase, runtime interface, graph, correction loop, live diagnostic or provider experiment was added. Reasoning, input/output limits, dispatch ordering, partitions, escalation and budgets are unchanged.

The implementation starts from repository `8043fd3` and the retained production baseline `d2ceac7cd85793baea732721f4e2b092c94be210`. The frozen binaries were copied before rebuilding and retained for original-schema replay. This report does not accept or relabel the historical MIXED campaign.

Machine-readable validation, proof fingerprints, schema measurements and integrity evidence are in [planner-nested-qualifiers.json](planner-nested-qualifiers.json).

## 2. First meaningful blocker

The original MIXED request 10, `contribution_clause_0799b094930035113a140f1e`, selected an executable request over `[179,247)` and an independent property over `[218,222)`. All eleven retained responses validate under their original schemas. Frozen-binary replay reproduces `INTENT_OPERATION_UNRESOLVED` at `/operations/@r_18bfa390e735cc81df86b716`, with zero provider dispatches and unchanged archived checkpoint/accounting.

The original response remains a flat answer. It has not been rewritten into a nested answer. Current-code replay stops at `REPLAY_EVIDENCE_REQUIRED` because matching current-domain receipts do not exist.

## 3. Root cause

The old projection treated intersecting support and property citations as conflicting authority, even when a property was contained in a complete execution request. The new schema expresses that composition explicitly. The engine validates containment and derives separate support/property projections.

This establishes a structural contract. It does not make semantic qualification infallible. Synthetic answers deliberately supply their linguistic interpretation; tests verify what those answers may authorize.

## 4. Authority analysis

- The complete requested-execution unit owns its positive execution qualification, effect binding, predicate and supporting references.
- A nested qualifier owns governing-property authority only. Its response contains an evidence reference, with no effect, execution basis, kind, necessity or parent-ID choice.
- The engine derives the qualifier's parent link. Neither that link nor singleton cardinality proves applicability.
- Coverage consumes support projections. It cannot promote qualifiers. Applicability still runs against established realizations; exact baseline/occurrence ownership is the existing deterministic shortcut.
- All eligible owned provenance is retained. A wrong preliminary property kind neither grants support nor vetoes property consideration. A partially overlapping unrelated record cannot supply child ownership.
- Declaration exclusions, necessity conflicts, optional omissions, baseline/resource ownership, revisions, admission dependencies and atomic commit keep their existing authorities.

Unlinked conflicting overlaps and exclusions still fail. A shared property can govern separate actions only through explicit applicability; shared evidence cannot merge occurrences or broadcast attachment.

## 5. Implemented action

`requested_execution` now contains a `request` with the existing predicate, support, effect, basis, owner and boundary fields, plus a bounded `qualifiers` array. Clause scope is fixed by the issued decision. Shared schema definitions factor the request contract without duplicating it for each permitted cardinality.

The existing six-unit allowance counts both parents and children. Cardinality alternatives keep schema sizing bounded; deterministic validation enforces the total. The four-reference support bound remains unchanged. Qualifiers must be properly contained in parent support evidence, cannot exhaust the predicate or execution evidence, and cannot form crossing composition. No text is trimmed or subtracted.

Persisted `PlanningContributionUnit` records remain flat and add optional `ParentRequestUnitId`. A parent ID hashes only its owned request facts; a child ID hashes its evidence and parent link. Units, contributions and provenance are sorted before proof hashing. Restored validation reconstructs the current proof rather than trusting supplied links.

| Contract | Current version |
|---|---:|
| Execution contribution | 5 |
| Operation admission | 15 |
| Realization coverage / governing applicability / effect | 3 / 1 / 7 |
| Occurrence / dependency / baseline projection | 1 / 1 / 1 |
| Runtime / source / declaration | 7 / 6 / 5 |
| Storage and encrypted namespaces | Schema-5 |

Canonical operation IDs remain effect/boundary-owned. Historical proofs require reassessment, with no migrated receipts or replenished accounting.

## 6. Validation performed

**Final gate results:** 3,724 solution tests passed; one optional provider test skipped; zero failures. The focused planner set passed 70 tests; focused harness checks passed 146. Additional nested-partition and re-signed-proof checks passed 1 and 5 tests. Solution/component and harness builds, both frontends, four NuGet packages, reference selfchecks, Native AOT/encrypted restart and trimmed Agent.Server persistence passed without new warnings.

The regression set covers nested projection, missing and forged parents, historical proof rejection, child ordering, exact duplicate notation, crossing/out-of-owner citations, predicate exhaustion, unit capacity, mixed-purpose clauses, wrong preliminary kinds, independent actions, shared applicability, necessity, canonical contract coverage, optional omission, baseline authority, partitions and restart.

The Native AOT planning smoke persists nested qualification before admission, reloads it through encrypted storage, and re-enters completed admission. The trimmed Agent.Server smoke verifies the generated parent-link contract through its encrypted EF-backed persistence path. Existing publish-local third-party/framework warning exceptions remain documented in the smoke and server READMEs; no analyzer suppression was added.

Historical replay, explicitly synthetic corrected fixtures and live evidence are reported separately. There is no new live evidence.

**Retained-MIXED limitation:** correcting the read composition does not extend the historical local action span. That span ends before `otherwise.`; full fixture acceptance requires exact owned fallback coverage. The detached synthetic run reports this remaining gap. It does not promote complete-clause context into new support or accept the historical campaign. The separate complete MIXED fixture uses explicitly identified retained inputs with complete owned rule evidence and new synthetic semantic answers.

## 7. Decision accounting

At the equivalent retained MIXED qualification checkpoint, nine interpretation decisions and two joint qualification decisions remain nine and two. Two requested-execution units remain two. The formerly independent overlapping property becomes one nested qualifier. This adds **zero composition requests** and removes **zero top-level semantic decisions**. The child projection is deterministic; qualification remains semantic work.

| Corrected synthetic path | Interpretation decisions/pages | Clause decisions/pages | Coverage decisions/pages | Applicability decisions/pages | Dependency decisions/pages | Relationship decisions/pages | Total nominal pages |
|---|---:|---:|---:|---:|---:|---:|---:|
| Complete LOCAL | 8/8 | 2/2 | 1/1 | 1/1 | 0/0 | 0/0 | **12** |
| Complete MIXED | 9/9 | 2/2 | 2/1 | 0/0 | 2/1 | 8/1 | **14** |

LOCAL has one support and one standalone property for `operation_e226ff9ed819b5a645e7d9de`: `record + threshold -> classifiedResult`. MIXED has one support per operation and one nested read qualifier; `operation_6b515f252d71b693829d2fe4` produces data for the same local result operation. Its two dependency questions select `data` for read → local and `none` for the reverse, with owned evidence. Later relationship schemas contain no `data` alternative.

Both paths have zero standalone identity decisions. The nested read qualifier attaches through exact existing occurrence ownership, not nesting. Canonical identities, fingerprints and complete snapshots survive read-only re-entry with zero calls and writes. The complete MIXED fixture uses `schema5-governing-applicability-mixed-diagnostics-1:mixed` inputs with explicitly synthetic current answers; it is distinct from the latest incomplete retained shape.

Remaining nominal allowances are four LOCAL slots and two MIXED slots. Largest measured post-interpretation input estimates are 3,340 and 3,363 tokens; qualification schemas are at most 5,552 bytes. The latest retained-shape composition also packs to fourteen pages but **fails full fixture acceptance** for its unchanged `[255,374)` action versus `[255,385)` rules clause.

Historical usage remains eleven verified calls: 29,195 input, 3,427 output and 2,114 reasoning tokens, with no partitions, escalations, repairs or unverifiable dispatches. Those are archived measurements, not usage from this implementation task.

Full synthetic-path packing, coverage, applicability, dependency and relationship counts are recorded separately in the JSON report. These are schema/packer measurements using synthetic answers, not provider-call savings or guaranteed live headroom. Historical accounting remains untouched; no live token usage was incurred.

## 8. Next gate

Review the offline results and the retained fallback-coverage limitation before authorizing live validation. Any fresh LOCAL or MIXED requires separate authorization. Stage 1 was not run.
