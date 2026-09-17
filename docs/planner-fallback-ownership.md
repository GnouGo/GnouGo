# Exact fallback ownership through joint qualification

## 1. Outcome

Implemented from `15ac49d0a496537e60b4a0bfc3ffcc9f9f7e9c55`, offline only. Joint clause qualification now has separate execution and governing source domains. Additional clause evidence can acquire governing authority, but cannot extend an action's support span, create an occurrence or acquire runtime necessity.

The validation results and integrity hashes are recorded in [the machine-readable report](planner-fallback-ownership.json). No live LOCAL, MIXED, Stage-1, provider probe or isolated request is part of this implementation.

## 2. First meaningful blocker

The retained MIXED interpretation selected action `r_b9c04327d386cd99d682be02`, `[255,374)`, through `runtime_d49eb5d4c9e144441d077fca`. It ends at `standard`. Complete-clause reference `r_d5142136f2b28a2fe1254696`, `[255,385)`, contains the fallback `otherwise.` at `[375,385)`, but no separate interpreted runtime-fallback record owns that meaning. The interpretation chose the shorter end; indexing and truncation did not remove the word.

This is category E: missing semantic ownership. The historical campaign remains stopped at its original overlapping-qualification failure. The separate missing-fallback assessment is not a retrospective change to that outcome.

## 3. Root cause

Previously, qualification considered complete clauses but selected evidence and checked completeness only within runtime-action references. Governing consumers also required runtime-action provenance. Clause context could therefore contain a fallback without any proof owning it, and broadening support would improperly turn context into executable authority.

## 4. Authority analysis

Execution support still requires an eligible runtime reference, a qualified execution request, a positive basis and current effect/boundary ownership. Canonical declaration exclusions apply to every role. Neither clause containment nor a semantic obligation's `Required` flag grants execution authority or capability necessity.

The same indivisible clause decision now separately qualifies exact governing references as `runtime_rule`, `runtime_condition`, `runtime_fallback` or `descriptive_property`, or explicitly accounts for irrelevant text. Existing condition/fallback kinds and grounding are validated. A broader property cannot erase their kind. Every non-whitespace clause position must be accounted for by selected units or exact canonical contract coverage. Production does not fill gaps, trim support or copy context into support.

Governing contributions may have no runtime parent. They retain current source bindings and, where applicable, exact semantic-obligation identity and grounding. They enter the existing applicability step after realization. Shared clause, workflow, nested composition and singleton cardinality still confer no applicability. Fallback-only evidence cannot establish a realization. Declaration omission defaults remain contract-owned.

## 5. Implementation

The existing `intent_operations` phase, durable pages and admission records remain the only storage and authority paths. `PlanningContributionSourceBinding` records the selected reference, owning clause scope and optional semantic-obligation grounding. These bindings are persisted on contribution units/proofs, operation assignments and applicability proofs; no new graph or authoritative snapshot collection was introduced.

| Proof | Current version |
|---|---:|
| Contribution | 6 |
| Governing applicability | 2 |
| Operation admission | 16 |
| Coverage / effect | 3 / 7 |
| Occurrence / dependency / baseline projection | 1 / 1 / 1 |
| Runtime / source / declaration | 7 / 6 / 5 |
| Storage / encrypted namespace | Schema-5 |

Canonical effect/operation IDs are unchanged. Fingerprints include sorted source provenance, semantic grounding, declaration coverage, selected units and realized applicability domains. Historical receipts remain tied to their original schemas; reassessment does not replenish accounting or repair/escalation allowances.

The corrected MIXED fixture supplies explicitly synthetic semantic answers: support remains `[255,374)`, and `[375,385)` is a separate source-only fallback. Its applicability is adjudicated against the realized classifier. The fake transport measures requests but makes no provider calls and writes no archived planning state.

## 6. Validation performed

**Final gate:** 3,733 solution tests passed, one optional provider test skipped, zero failures. The planner component passed all 1,128 tests. Focused ownership/applicability/coverage checks passed 70 tests; focused harness checks passed 146. The complete solution and harness builds, both frontends, four packages, reference selfchecks, Native AOT/encrypted restart and trimmed EF persistence passed under the existing documented warning exceptions, with zero reported build/publish warnings.

Historical replay checked all eleven retained answers under their original schemas and reproduced the original overlap stop with zero dispatches. Current strict replay stopped at `REPLAY_EVIDENCE_REQUIRED`; no old answer was interpreted under a new schema. All 84 previously retained reports and 23 preserved DLL hashes remain unchanged, as do the declaration fixtures and archived checkpoint/accounting. The checkpoint fingerprint remains `7a754d36d480f1a54dc68dfcc50d45344b698e030c67b6760dbf4b89460311c3`.

Both explicitly synthetic fixtures passed current acceptance. Completed re-entry reproduced contribution, applicability, coverage, dependency, admission and snapshot proofs with zero provider calls and zero checkpoint writes. Historical replay, synthetic qualification/dataflow answers and live evidence remain separate; no new live evidence was obtained.

The regression suite includes exact fallback composition, incomplete clause rejection, fallback-only clauses, existing semantic kind/grounding, source enumeration, partial restart, forged bindings, optional capability necessity, declaration exclusion, nested qualifiers, independent operations and current applicability. Older synthetic fixtures now explicitly account for their otherwise omitted source text; production does not apply those fixture assumptions.

Native AOT planning/encrypted restart and trimmed Agent.Server persistence smokes retain source-only fallback references with no fabricated runtime parent. Existing documented publish-local framework/third-party warning exceptions remain unchanged.

## 7. Decision accounting

Qualification remains one indivisible decision per eligible clause. Supplementary governing-kind selections are embedded semantic work, not eliminated work or separate interpretation calls. The retained MIXED fixture keeps nine interpretation decisions and two joint clause qualification decisions. The newly qualified fallback needs one semantic applicability decision; exact external-occurrence ownership can still attach the nested read qualifier deterministically.

The detached synthetic LOCAL path measures 12 nominal requests (8 interpretation, 2 qualification, 1 coverage and 1 applicability), with four slots remaining. The corrected retained MIXED shape measures 14 through admission and 15 including relationships, leaving two and one slots respectively:

| MIXED work | Logical decisions | Nominal requests |
|---|---:|---:|
| Interpretation | 9 | 9 |
| Joint clause qualification | 2 | 2 |
| Realization coverage | 2 | 1 |
| Governing applicability | 1 model; 1 exact-owner attachment | 1 |
| Boundary scope / standalone identity | 0 / 0 | 0 |
| Admission dependencies | 2 | 1 |
| Downstream relationships (no `data` alternative) | 8 | 1 |

The two execution-request units produce two effects. Qualification also contains one nested read qualifier, one source-only fallback and one explicitly synthetic no-operation-relevance unit for remaining clause text. The fallback selection is embedded qualification work, followed by one applicability decision. The largest post-interpretation request estimates 3,934 input tokens for MIXED and 3,921 for LOCAL; qualification schemas are at most 6,521 bytes. Provider usage is absent for these fake transports, not estimated as actual usage.

Final fingerprints and measurements are in the machine-readable report. They are nominal synthetic first-completion paths, not provider-call savings or guaranteed live headroom. The sixteen-call allowance, low reasoning, 12,000 input ceiling, 9,600 dispatch target, 8,192 normal output, bounded 16,384 escalation, paging and repair budgets are unchanged.

## 8. Next gate

After all offline checks pass: one fresh LOCAL, requiring separate authorization. No live execution is authorized or performed by this report.
