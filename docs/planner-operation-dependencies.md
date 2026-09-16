# Admission-owned operation data dependencies

## Outcome

**PASS — offline implementation and validation.** No LOCAL, MIXED or Stage-1 session is included. This is admission evidence, not end-to-end workflow success.

The implementation starts from `445861c` on `feat/deterministic-planner-v2`. The retained provider evidence used production `2f8b177ec6076cfdedbf524bd72a01d3f1b457d7`. Archives remain immutable. This change does not alter declarations, necessity, confirmation, reasoning profiles, token limits, recursive partitioning or singleton escalation.

## First meaningful blocker

The retained LOCAL case `schema5-realized-governing-diagnostics-rerun-4:local` selected the same operation as effect and producer in realization request 11 and governing request 12. Both original responses were schema-valid. Admission correctly rejected the self-edge with `INTENT_OPERATION_UNRESOLVED`, “Grounded effects contain a dependency cycle,” at `/operations/@operation_1d213d8df89c1c565c28773f`.

## Root cause

Realizations, governing mappings and later relationship analysis independently selected operation data dependencies. Their domains differed. Scope and realization filters did not establish that a selected effect was an upstream result producer. The repeated impossible choices across those domains warranted the architecture review rather than another self-ID exclusion in one field.

## Authority analysis

Operation admission now owns operation-to-operation result-consumption dependencies. Policies, permissions, resource ownership, failures and other behavior relationships remain downstream. An admission edge is not an executable binding, schema-compatibility proof or permission to access a resource.

Pre-patch self-review:

1. **Invariant:** a canonical operation cannot depend on itself as an upstream producer.
2. **Owner:** the admission dependency domain and proof validator.
3. **Known facts:** canonical IDs already identify self, scope and realized endpoints.
4. **Bad choice:** independent effect/producer arrays overlapped and later relations added a third authority.
5. **Elimination:** remove those arrays and derive consumer-specific domains after canonical realization.
6. **Generality:** owned references, canonical identities and established interfaces; no fixture keywords or IDs.
7. **Preserved risks:** independent operations, multiple producers, cross-workflow interfaces, resource ownership and baseline authority.
8. **Independence regression:** two transformations remain distinct without an inferred edge; a sole other operation is insufficient proof.
9. **Authority reduction:** two embedded producer selections disappear for the captured singleton. Remaining semantic result consumption has one owner.
10. **Stop rule:** another verified semantic blocker in this authority class requires architecture review before a targeted producer patch.

## Proposed action — implemented

### One proof before atomic admission

After realizations, canonical identity, governing attachments and revision assessment, `intent_operations` resolves dependencies before committing operations. Intermediate candidates remain in the existing decision pages and staged obligations. There is no additional graph, service, collection or planner phase.

Effect response schemas no longer expose `producers`. Historical `Effect.Producers` is retained for audit/deserialization but must be empty in current effect proofs. Later relation schemas cannot select `data`. Data relations project from the admission dependency proof and already validated public-input consumption.

Exact baseline bindings establish required edges deterministically. A baseline caller-result edge is labelled deterministic interface only when its referenced callee exists in the supplied baseline. Exact baseline-to-baseline absence is a structural exclusion. Missing required baseline producers or missing caller interfaces stop before model dispatch.

Other candidate pairs contain only surviving canonical operations in the same execution scope. Self, unrealized, inaccessible and superseded endpoints cannot be selected. A cross-workflow value is represented through an already established caller-side operation; dependency assessment cannot create that interface or expose a foreign declaration directly.

Bounded semantic responses select `data`, `none` or `unresolved` for fixed producer/consumer IDs. Positive and negative selections cite owned producer and consumer evidence. The complete domain fingerprint binds their interpretation. `none` records that the bounded evidence requires no explicit producer relationship, not a proof that operations are semantically independent. Known required edges have no model decision and cannot be suppressed with `none`. Unresolved evidence never becomes an empty proof.

### Order and persistence

Every semantic pair uses the same immutable complete operation set and pre-established structural facts. All answers are staged before combined cycle validation. No earlier semantic selection removes a later alternative or causes traversal order to choose a different acyclic graph. Known structural paths can exclude impossible reverse edges before any model selection. Contradictory cycles stop as a whole.

Canonical sorting normalizes edges, evidence references and fingerprints. Dependency proof identity excludes page IDs, packing, arrival order and request accounting. Reordered owned sources, operation/pair iteration, page packing and restart produce identical canonical dependency proofs for equivalent evidence. Editing source content or changing canonical identities is a governing change, not an ordering permutation.

Each operation admission contains dependency proof **1**, with complete pair assessments, per-edge origin, evidence, decision lineage and domain/proof fingerprints. Edge origins are deterministic baseline, deterministic interface or model semantic selection. A complete singleton has an engine-established empty operation-producer set.

Effect proof advances **2 → 3**, operation admission **6 → 7**. Runtime/source proofs stay **4**, declaration proof **5**, storage **Schema-5**. Operation IDs are unchanged. Historical proofs require explicit reassessment; receipts, budgets and correction/escalation allowances are not migrated or reset.

### Tests and replay boundaries

Generic regressions cover excluded/forged endpoints, required baseline/interface edges, missing producers/interfaces, bounded negative evidence, independent transformations, multiple producers, shared rules, cycles, downstream non-data roles, stale proofs, changed source/pair/page order and partial-checkpoint restart. The published Native AOT smoke closes after journaled dependency decisions and resumes twice without another dispatch; the trimmed Agent smoke checks encrypted edge origins and proof metadata.

The historical audit uses original schemas and receipts only. Under changed request identities, strict replay stops at unavailable evidence. A separate `audit-dependency-fixture` command preserves captured effect assignments and removes only `producers` in explicitly synthetic answers to the new schemas. It never constructs a provider transport or writes an archived record.

## Validation performed

Final measured results are recorded in [the redacted offline report](planner-operation-dependencies.json). Historical, synthetic, test and published-binary evidence are separate. No live evidence is claimed.

The full solution gate uses one MSBuild worker because the initial parallel invocation encountered concurrent writes to dependency/static-web-asset outputs. The persistence-focused trimmed publish uses the existing `SkipBundledServerTools` option: the initial default publish entered an unrelated Playwright Chromium download, timed out and was stopped. Neither issue required a production behavior change or a new warning suppression.

## Decision accounting

The historical checkpoint contains 12 calls and 25 model decisions: 22 interpretation, two realization and one governing decision; zero standalone identity decisions. Two returned mapped answers selected producer arrays.

The corrected singleton retains its realization/governing selections and public dataflow but has **zero producer fields**, **zero dependency-model decisions**, **zero operation-producer edges**, and **zero standalone identity-model decisions**. The empty producer set is engine-established. Grounding requests remain necessary; removing two fields is not two saved calls. Synthetic calls and unknown actual-token usage are reported separately.

Mixed workflows retain bounded semantic dependency decisions when structural evidence is insufficient. Those are accounted under `operation_dependencies`, separate from effect grounding and downstream relationships. No net live call/token savings are claimed.

## Next gate

**LOCAL**, only after separate authorization. Stop after offline reporting. A new verified dependency-authority failure requires another architecture review, not another targeted filter.
