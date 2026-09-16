# Structural baseline projection — offline implementation

## 1. Outcome

**Category C structural-authority blocker corrected offline.** Implemented from `eb94f52dfb421bc3062c72c4a4f02eeb78535079`, whose production implementation was `1b5513c28032c718eeb9ea4179e5c7981c51bbba`.

`Request.Baseline` supplies structural evidence directly. No serialized graph fragment is an interpretation decision. This iteration made **zero provider requests**. LOCAL, MIXED and Stage 1 were **not run**.

Machine-readable evidence, source/binary hashes and check results are in [the offline record](planner-structural-baseline.json).

## 2. First meaningful blocker

The retained `schema5-admission-dependencies-diagnostics-1:local` case failed on `interpret_a211f8afaa7bca2154080a84`: a 49-character baseline serialization tail was classified as runtime `unresolved`. The answer satisfied its original schema. Validation stopped with `INTENT_OPERATION_UNRESOLVED` at `/operations/@runtime_ea48b9c2d364d80145d7e033`.

The original checkpoint fingerprint is `08e58ae950a0da818e8f48a5e8bb387257327ce806fc4f44ad42e99e64cdf7c2`; the archived budget fingerprint is `c3feba44942c05206f03c5173bdd54c9d3dec2d5c6342ac62c222255ab7325b0`. All eleven receipts remain valid against their **original** schemas. Effect grounding and dependency admission were not reached in that run.

## 3. Root cause

The previous boundary serialized the authoritative graph, split it into general prose spans and asked the model to rediscover structural facts already used by declaration seeding, baseline-node ownership and dependency validation.

The implementation selects architecture **B: typed baseline projection**. Improving JSON chunking would retain duplicate authority; moving interpretation later would delay the same ambiguity. Neither is needed to recover information already present in the baseline.

## 4. Authority analysis

The approved ten self-review answers remain the implementation boundaries:

1. **Failed invariant:** known baseline structure cannot require a model-owned execution-scope classification.
2. **Owner:** the baseline-to-source-evidence boundary.
3. **Known facts:** ports, schemas, defaults, entrypoint, node coordinates, nesting, conditions and bindings.
4. **Why exposed:** serialized structural objects entered general prose interpretation.
5. **Deterministic elimination:** project structural units and exclude them from model response domains.
6. **Generality:** typed fields and owned coordinates; no tool/provider names, fixture words or diagnostic-ID branches.
7. **Risks preserved:** executable baselines, nested/finalizer nodes, opaque effects, annotations and revisions remain subject to exact authority checks.
8. **Independence:** distinct baseline nodes retain distinct canonical operation IDs even when their descriptions are identical.
9. **Authority change:** the baseline owns structural classification; annotations may add owner-bound governing evidence only.
10. **Recurrence rule:** another verified blocker in this structural-authority class requires architecture review before another targeted fix.

## 5. Implemented change

`PlanningBaselineProjection` is a derived view inside Flow.Planning, not a stored graph or service. It issues structural units for the graph, workflows, ports and exact nodes, and text sources only for designated summary, purpose and schema-description fields. Expressions, function code, literal values and metadata stay typed. Structural unit content is resolved from the current baseline; only references and provenance are retained in evidence.

`PlanningReference.Baseline` records projection version, baseline fingerprint, structural owner coordinates and optional annotation field. `EngineBaseline` is appended to the evidence-origin enum. Complete structural units bypass the 256-character prose splitting path. Existing text-span coverage remains for requested behavior, answers and host policy. Structural coverage and all reference consumers verify current baseline ownership.

Port-only baselines produce contracts, no operations and no structural model questions. Declaration seeding, exact public names, requiredness, default-on-omission semantics and public IDs are unchanged. Native nodes derive runtime kind from existing Flow executor semantics and retain their exact baseline coordinates. Node annotations govern their own realized node; port constraints can target only their canonical port. Annotation schemas cannot create ports, operations or runtime ownership. Policy/relationship domains use the same structural owner restriction; confirmation semantics and enforcement are unchanged.

Opaque nodes retain exact node-owned evidence. If their executor does not provide a proven execution effect or resource ownership, admission stops at that baseline node before effect requests; descriptions do not fill the gap. The current baseline graph does not carry a general MCP effect/ownership contract. This change does not infer one from schemas, names or purposes.

`ReviewedBaselineBehavior` stays review context and never replaces `Request.Baseline`. Exact baseline bindings continue through the existing admission dependency proof. No data edge authority, phase, operation graph or public runtime interface was added.

Proof versions: baseline projection **1**; source **5**; runtime evidence **5**; operation admission **8**. Declaration **5**, effect **3**, dependency **1**, storage and encrypted namespaces **5** are unchanged. Baseline canonical operation coordinates and public declaration identities are preserved. Historical structural-fragment decisions remain audit evidence. Explicit reassessment is required; no archives, receipts, budgets, repair allowances or escalation consumption are migrated/reset.

## 6. Validation performed

**Historical evidence:** before rebuilding the benchmark, the retained original binaries replayed the same stop with zero provider dispatches and unchanged archived state. The new read-only audit validates the eleven original responses against their original schemas. Strict replay with the new request builders stops at `REPLAY_EVIDENCE_REQUIRED`, with zero reused mismatched receipts. Original records and fixture files remain unchanged.

**Synthetic evidence:** fifteen new generic baseline regressions cover large/punctuated port schemas, zero structural interpretation, exact native/nested/finalizer nodes and bindings, opaque effect/ownership stops, identical-description independence, annotation target domains, expressions/literals excluded from prose, tenant/owner/contract/removal invalidation, explicit revision retirement, reviewed-context separation and receipt/accounting reuse. Existing corrected classifier fixtures are explicitly re-assessed as synthetic current-proof fixtures. They retain two inputs, one output, threshold default `100`, one required local effect and the existing governing/dependency assertions. No synthetic response replaces a historical receipt.

**Offline gates:** 3,509 solution tests passed with one optional provider-test skip, including 1,008 planner tests. Focused Agent/harness/persistence checks passed. The solution and benchmark builds, Agent/Flow frontend builds, four affected package builds, synthetic LOCAL/MIXED fixture selfchecks, six classifier/batch reference cases and eighteen CodeReview reference cases passed. Published Native AOT planning/encrypted restart and trimmed Agent persistence checks include structural provenance and the new origin. Commands and log hashes are in the offline record.

Build/publish logs contain no warning diagnostics under the existing documented Jint/EF/Blazor publish exceptions. No new warning suppression was added. A smoke-only assertion that assumed one reference was updated to select its original reference after adding structural provenance.

**Live evidence:** none in this iteration. These results do not establish fresh declaration convergence, live admission convergence, executable construction or Stage-1 success.

## 7. Decision accounting

At the equivalent retained classifier source checkpoint:

| Measurement | Historical | Current offline domain |
|---|---:|---:|
| Interpretation decisions | 22 | 17 |
| Baseline structural model decisions | 5 | 0 |
| New baseline annotation model decisions | 0 | 0 |
| Engine-projected structural units | 0 | 5 |
| Verified historical requests / current packed pages | 11 requests | 10 planned pages |

The five removed model decisions were four `contract` answers and one `unresolved` answer. The fixture has no executable baseline nodes or descriptive annotations. Current counts include eight requested-source decisions and nine host-policy semantic decisions; host runtime scope remains engine-owned.

The page count is an offline packing measurement. It is **not** five saved provider calls or a live call-savings result. Effect realization, governing grounding, identity and admission dependency decisions remain separate. The existing synthetic classifier still has zero standalone occurrence-identity decisions and zero operation-producer model decisions. No reasoning, output limits, paging, escalation or repair budgets changed.

## 8. Next gate

**LOCAL, requiring separate authorization.** Freeze the validated implementation and binaries before any later diagnostic. This task stops after offline implementation and reporting; MIXED and Stage 1 remain not run.
