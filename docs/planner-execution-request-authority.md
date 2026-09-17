# Canonical execution-request authority

## 1. Outcome

Implemented from `e1fe4687574936d5ce2632d7e30b84f70adbeb19`, offline only. Canonical execution requests are established before property qualification, in bounded cohorts of owned clauses with intersecting candidate-effect domains. Support is a deterministic projection of those request proofs. Property responses cannot create or strengthen execution authority.

No live LOCAL, MIXED, Stage-1, provider probe or isolated request is included. Historical campaigns remain unchanged. The [machine-readable validation report](planner-execution-request-authority.json) records proofs, accounting, source/binary hashes and validation artifacts.

## 2. First meaningful blocker

The retained LOCAL has two separate findings. Request 11 semantically misqualified the complete deterministic/local description `[539,599)` as requested execution. Request 13 then proposed `[7,28)` as a nested qualifier of request `[29,57)`, which fails containment. The latter was the terminal validation failure; neither proposal reached admitted realization coverage.

The current implementation removes later opportunities to create request authority and excludes impossible children from fixed-parent response domains. It does not claim that a new proof record makes natural-language adjudication infallible. A description misclassified by the canonical request adjudicator would remain a verified semantic failure requiring architecture review.

## 3. Root cause

Preliminary runtime extraction implicitly assessed requested performance, while each contribution clause could independently select a request predicate, support spans, effect and positive basis. A possible result effect and compatible runtime facts provided an eligible domain, but no prior canonical proof of requested execution. Coverage consumed this answer; it did not independently reclassify requested performance.

The correction establishes that semantic authority once. Effect ownership, occurrence boundaries, declarations, necessity and preliminary kinds remain distinct facts.

## 4. Authority analysis

The engine retains exact source ownership, declaration exclusions, finite effect domains, occurrence/baseline coordinates, locked execution facts and containment validation. A bounded request answer selects positive request predicates and execution/result-selection references, or explicitly records that candidate evidence establishes no request. Ownership and the applicable positive basis are fixed by the selected issued effect; the response cannot invent them.

Request judgments remain semantic unless exact current baseline execution supplies deterministic authority. Non-request outcomes establish neither governing relevance nor applicability. Negative outcomes are bound to their preliminary runtime record: an incompatible overlapping hint cannot erase a positive request supported by different valid provenance.

Later property qualification has no `requested_execution` alternative. It accounts for the complete clause using fixed request-backed evidence, exact declaration coverage, governing properties and exclusions. A nested property can select only properly contained references for its issued fixed parent. Independent source-only fallback evidence remains representable outside that parent. Nesting, shared clauses and singleton cardinality confer no applicability.

## 5. Implementation

The new `PlanningOperations.ExecutionRequests.cs` partial derives deterministic connected cohorts without merging operation identities. Each cohort is one indivisible decision in existing durable pages. It records all positive and non-request outcomes; partial or unresolved authority stops before property qualification. Exact structural baseline nodes produce deterministic request proofs.

`PlanningExecutionRequestProof` is carried on existing operation admission records. Contributions and semantic units retain a current request-proof link. Request predicates, exact support references, source/runtime provenance, effect/owner/boundary references, domain fingerprints, origins and decision lineage survive persistence. Runtime records retain their existing necessity and execution contracts; request proof domains bind those facts without another model-selected contract.

The response uses compact reference fields (`s/u/r/q/p/e/t/v/a/z`) to preserve the existing response-sizing allowance. Their meanings are supplied in the request context. It selects no capability, tool, public dataflow binding or governing target. No token or decision allowance changes.

| Proof | Version |
|---|---:|
| Execution request | 1 (new) |
| Contribution | 7 |
| Admission | 17 |
| Coverage / applicability / effect | 3 / 2 / 7 |
| Runtime / source / declaration | 7 / 6 / 5 |
| Occurrence / dependency / baseline projection | 1 / 1 / 1 |
| Storage / encrypted namespaces | Schema-5 |

Canonical operation IDs remain effect/boundary-owned. Sorted cohort membership, source references, outcomes and units determine proofs independently of enumeration or packing. Admission includes negative outcomes as well as positive requests. Current validation reconstructs request and contribution proofs from matching pages and checks persisted records exactly. Historical requested-execution units cannot independently authorize current support.

## 6. Validation performed

The final solution suite passed **3,752 tests**, with **zero failures** and **one optional provider-test skip**. This includes all **1,136 planner tests**. Focused authority/contract/fallback/composition checks passed **72 tests**; focused harness checks passed **142**. Solution and harness builds, both frontends, four component packages, reference/harness selfchecks, Native AOT/encrypted restart and trimmed EF persistence passed with zero reported warnings under the existing documented publish exceptions.

Original-schema replay checked all eleven completed answers and both verified output-limit receipts, reproducing the historical containment stop with zero dispatches. Current strict replay stopped at `REPLAY_EVIDENCE_REQUIRED`. The archived checkpoint, cumulative budget, original reports, retained artifacts, declaration fixtures and 23 frozen DLLs remain unchanged. Historical replay is separate from explicitly synthetic corrected fixtures; no synthetic answer is represented as a retained receipt or live result.

The regressions cover fixed support projection, descriptive-only property qualification, exact contract exclusions, positive/negative provenance, independently owned effects, source-only fallback, fixed-parent schema containment, foreign/stale references, necessity and baseline authority. Partial request checkpoints reuse completed pages and retain existing reservations; different packing and source enumeration preserve request proof fingerprints. Completed admission re-entry rejects provider calls and checkpoint writes.

Native AOT/encrypted restart and trimmed EF persistence exercise request proof 1, contribution 7 and admission 17 with source-generated serialization. Existing publish-local warning exceptions remain unchanged.

Both corrected synthetic fixtures pass acceptance and preserve the historical action references. LOCAL admits one required `local_processing` operation, `operation_e226ff9ed819b5a645e7d9de`, consuming `record` and `threshold` and producing `classifiedResult`. Two request-backed supports and two governing contributions account for the action, rules, fallback and description. The description is explicitly not a request and supplies no support.

MIXED retains required external read `operation_1169739a01f8b1ee16abd853` and the same canonical local result operation. The read consumes `sourceId`; its result flows through the admission-owned dependency to the local transform, which also consumes `threshold` and produces `classifiedResult`. Each operation has one support and one governing contribution. The local support remains `[255,374)`; fallback `[375,385)` remains a separate source-only governing contribution. The read qualifier's applicability follows its existing exact occurrence ownership; the fallback requires a semantic applicability decision. No implicit data authority is added downstream.

Both fixtures validate request/contribution/coverage/applicability/dependency/admission proofs after reload and reproduce the complete snapshot with **zero provider calls and zero checkpoint writes**. Operation IDs match the preceding corrected fixtures. The report records 120 production-source hashes and 23 tested DLL hashes, separately from the retained frozen binaries.

## 7. Decision accounting

The retained three-clause LOCAL had three separate opportunities to establish requested execution. The corrected synthetic fixture has one canonical request cohort and zero later model choices that grant executable support. Property qualification remains a separate dependent responsibility. Two positive request bindings project two supports for one classifier; the descriptive clause has an explicit non-request outcome and governing qualification.

The nominal measurement uses unchanged production packing, budgets and reasoning. It counts interpretation, request adjudication, property qualification, coverage, applicability, dependencies and downstream relationships separately. Synthetic transports provide no actual provider token usage. Historical truncation/escalation behavior cannot predict these new request shapes, and no provider-call savings or guaranteed headroom are claimed.

| Work | LOCAL decisions / nominal calls | MIXED decisions / nominal calls |
|---|---:|---:|
| Interpretation | 8 / 8 | 9 / 9 |
| Canonical request adjudication | 1 / 1 | 2 / 1 |
| Property qualification | 3 / 2 | 2 / 1 |
| Realization coverage | 1 / 1 | 2 / 1 |
| Governing applicability | 2 / 1 | 1 / 1 |
| Operation dependencies | 0 / 0 | 2 / 1 |
| Standalone identity / boundary-scope | 0 / 0 | 0 / 0 |
| Downstream relationships | Not included in LOCAL admission gate | 8 / 1 |
| **Through admission** | **15 / 13** | **18 / 14** |
| **Complete measured diagnostic path** | **15 / 13** | **26 / 15** |
| **Remaining nominal call allowance** | **3** | **1** |

LOCAL's one request cohort contains two positive bindings and one explicit non-request outcome. MIXED's two cohorts contain two positive bindings. Each fixture projects two support contributions deterministically and qualifies two governing properties. Projection and attachment work are not additional model decisions. MIXED's two semantic dependency answers produce exactly read → local plus a negative reverse-pair outcome; they are not inferred from there being two operations.

The LOCAL request schema is 5,123 bytes with an estimated answer allowance of 1,824 tokens; MIXED request schemas are 1,793 and 1,937 bytes, each estimated at 622 answer tokens. Largest estimated inputs among the synthetic admission/relationship dispatches are 3,737 and 5,018 tokens respectively. Interpretation contributes eight/nine independently measured initial pages. These are engine estimates, not provider usage.

At the equivalent pre-coverage LOCAL checkpoint, logical decisions increase **11 → 12**, while nominal initial pages remain **11 → 11**. Three clause-local request-authority scopes become one request cohort and zero later support-granting choices. The retained thirteen verified calls included two successful singleton escalations; that historical count cannot be compared as a measured saving against synthetic first-attempt envelopes. Synthetic fixtures use no partitions, escalations, repairs or unverifiable dispatches.

## 8. Next gate

**One fresh LOCAL, requiring separate authorization.** The measured mandatory paths fit within sixteen calls, so no budget change or skipped work is proposed. MIXED and Stage 1 remain unrun live.
