# Realization coverage v1 — fresh LOCAL validation

## 1. Outcome

**VERIFIED BLOCKER — category C. LOCAL did not pass.** Exactly one fresh LOCAL ran under `schema5-realization-coverage-diagnostics-1`, with production frozen at `504c0b91e530a6fcd41794630d2407ad3c5e2413`. No production code, fixture, model, reasoning, limits or budgets changed. MIXED and Stage 1 were not run. There was no replacement start, provider retry or isolated experiment.

Admission committed one required `local_processing` operation, `operation_e226ff9ed819b5a645e7d9de`, consuming canonical `record` and `threshold` and producing `classifiedResult`. Coverage is complete and the producer dependency set is empty. The acceptance failure concerns descriptive evidence being granted executable supporting authority, rather than missing dataflow, duplicate operations or incomplete coverage.

The [redacted machine-readable report](planner-realization-coverage-local-1.json) preserves request/receipt fingerprints, decision accounting, proof fingerprints, validation evidence and integrity checks. Exact original requests and responses remain in the encrypted journal.

## 2. First meaningful blocker

The LOCAL acceptance check stopped with `DIAGNOSTIC_DESCRIPTIVE_EVIDENCE` during admission assessment, at location `$`: “Descriptive local evidence must attach to the established effect.”

Request 3, `interpret_65f31e6ea3c4661266f71958`, classified “This is deterministic, local, in-memory business processing.” as `implementation_policy` in its semantic facet and `local_behavior`, `local_processing`, `evidence: action`, with unspecified necessity in its runtime facet. The resulting evidence is `runtime_a20490381bf1a4bbd2199096`, clause `r_750a2436fdca608debd9bc99`.

Request 9, coverage decision `coverage_69f1587bbdbb0b85fd19ff03`, selected `mapping_e497ce0b3855326079369084`. That complete mapping used both the requested classification action and the descriptive statement as executable support. The detailed classification rules became governing evidence. All nine answers satisfy their original schemas.

The description is therefore present on the correct effect, but its disposition is `supports` / `realizes`, rather than `attach` / `governs`. No second descriptive operation was created. The stronger authority assigned to that description fails the established LOCAL requirement.

Relevant fingerprints:

| Evidence | Request fingerprint | Receipt fingerprint |
|---|---|---|
| Descriptive interpretation | `9278c981e79fc24fce09cd9367b67aa88c705da4e14bcbce7d4a42ad832ba64d` | `b4fe67354fd88c1958c62355275753b9e00a5115887047028ee839287f6cb1da` |
| Complete coverage | `d43fa5b704b8f93caaf4a9686d62a31206357da18b46a5a730daaed8c2011c79` | `6b901172a41c1c70e9ac3d6216a0638c17b546bc09b61557f29b3cb4881a370d` |

## 3. Root cause

Preliminary runtime interpretation labelled descriptive implementation evidence as an action. Coverage subsequently treated that eligible action as possible executable support, and the selected mapping retained that authority. Current structural validation proves owned references, complete coverage and consistent effect/dataflow identity; it does not independently establish that this descriptive contribution expresses execution rather than describing execution.

This is schema-valid semantic evidence, not provider failure, output exhaustion or call-budget exhaustion. The new complete-set coverage mechanism did prevent collective deferral: one supported result realization committed. The remaining issue is the support-versus-governing authority of one contribution.

## 4. Authority analysis

The engine owns effect identity, source ownership, declaration coverage, scope, necessity resolution and dependency authority. The model selected the runtime evidence role and a permitted complete coverage mapping. Canonical identity and dependencies remained deterministic.

The engine knew the single compatible result effect and could attach governing evidence to it. It did not have an independent current proof excluding the descriptive span from executable support once interpretation called it an action. An `implementation_policy` label alone cannot generically exclude separately evidenced execution from every mixed-purpose clause. A keyword exclusion or silently rewriting the receipt would not be justified.

The failed invariant belongs to the runtime-evidence-to-executable-support boundary. Given the recurring action/governing authority class, architecture review is required before another targeted correction. No production patch was made.

## 5. Proposed action

Preserve the stopped case and review executable-support authority versus governing evidence. Keep the successful identity, scope, coverage, necessity, declaration and dependency invariants. Do not rerun this campaign, weaken the acceptance check or promote the result to LOCAL PASS.

## 6. Validation performed

**Prerequisites rerun:** 3,584 solution tests passed across 29 assemblies, zero failures and one optional provider-test skip. Planner: 1,038 passed. Focused harness/journal checks: 84 passed, including five new coverage-gate cases. Harness and test builds used `BuildProjectReferences=false` and emitted zero warnings. Reference selfchecks passed six classifier/batch and eighteen additional cases. Published Native AOT planning/encrypted-restart and trimmed Agent.Server persistence smokes passed.

**Retained frozen evidence:** the earlier complete production build, frontend builds, four package builds and publish logs were hash-verified against the offline report. This distinguishes the newly rerun checks from retained evidence. Nine recorded production source hashes, all 22 production DLL hashes, both published binaries, four packages and the unchanged fixture source hashes matched before and after the run. The harness hash was separately frozen as `5366cf4353e50293b9328836e6c4104b97a417e443eca767acaadf4f819b648d`.

**Currency prerequisite:** the existing exchange-rate provider returned a valid USD→EUR quote dated 2026-09-16 in one check. No quote was injected and no model probe was sent.

**Live:** eight interpretation requests and one coverage request completed with verifiable receipts. Typed policy projected eight clauses into nine semantic obligations without host interpretation calls. Five structural baseline units were engine-owned with no structural interpretation calls or baseline operations. Runtime scopes were resolved. The unchanged declaration fixture supplied ports and attachments; fresh declaration convergence is not claimed. Preservation remained output declaration evidence.

**Read-only evidence:** all nine retained replies were checked against their original schemas with zero findings. Completed-admission re-entry used the persisted proof without replaying or dispatching model requests. A separate post-blocker detached reload invoked the frozen verification path with rejecting transport and checkpoint writer: zero provider calls, zero writes, identical full snapshot and admission/coverage/dependency fingerprints. The same descriptive acceptance failure reproduced. The successful read-only proof check does not override the stopped live assessment.

The post-blocker persisted snapshot fingerprint is `2911d870e0b075689653f3ab1dd9ab79010ed34bdaffd015df1072fcd0889358`; its enclosing encrypted-record content fingerprint is `3bc5623059a6fdc85a73df166668f96e82a06fae41d42c31635d60cd2dcce8c4`. Coverage v1 fingerprint: `15a3c3bca79a3d3f5f9117bd742fabb89eb3b05b79c2afd3d87d5b6fc8062ef9`. Global admission fingerprint: `1d80977869279b74fccbd0a673e1b783a7caab3162396b404ac16f890272a7bc`; operation admission v10 fingerprint: `5a8517fca3dd3dd7d88b0629a03f379690cf5f62f4b6d0bbcddb371b7aef1da7`. Dependency v1 fingerprint: `47ffeeb4136fe4e7049539565f342590c347c60f35e017596b77bbf7c0564c44`.

Current checkpoint/budget and previous archive accounting were unchanged by read-only inspection. The historical archive fingerprint remains `93d2d9d2970804a03c2228b1edd9424b095cd47d21b49dba921e3141bf8bfc1b`. No synthetic response was used in live execution or receipt validation. Synthetic selfchecks remain separately labelled prerequisite evidence.

## 7. Decision accounting

| Class | Model decisions | Verified provider calls | Input tokens | Output tokens | Reasoning tokens |
|---|---:|---:|---:|---:|---:|
| Interpretation | 8 | 8 | 25,336 | 2,514 | 1,666 |
| Joint realization coverage | 1 | 1 | 1,802 | 549 | 426 |
| Additional governing | 0 | 0 | 0 | 0 | 0 |
| Occurrence boundary/scope | 0 | 0 | 0 | 0 | 0 |
| Standalone occurrence identity | 0 | 0 | 0 | 0 | 0 |
| Operation dependencies | 0 | 0 | 0 | 0 | 0 |
| **Total** | **9** | **9** | **27,138** | **3,063** | **2,092** |

Interpretation used eight initial pages. Its eight runtime facets are embedded in those calls and are not additional provider decisions/calls. Coverage assessed three action-labelled contributions jointly. One canonical operation identity was assigned deterministically; three contribution mappings materialized as two supporting contributions and one governing attachment. The governing rules include the classification conditions and fallback. The descriptive contribution is one of the two supports, causing the blocker.

The single effect owns `classifiedResult`; public inputs remain declaration dependencies. The admission dependency proof has an engine-established empty producer set. There is no external, lifecycle or human operation, and no authority from `Effect.Producers`.

Nine coordinator reservations, nine durable budget reservations and nine journal requests correspond to the nine verified receipts. **Seven of sixteen call slots remain.** Output partitions: **0**. Singleton escalations: **0**. Semantic repairs: **0**. Unverifiable dispatches: **0**. Usage is known for every request. Largest estimated input: **5,648** tokens; largest actual input: **4,066** tokens. All requests used low reasoning and the normal 8,192 output ceiling.

This validation task added and removed zero planner decisions. No provider-call savings are inferred from previous synthetic envelopes or removed fields. The live architecture demonstrated zero standalone identity and dependency questions, but did not satisfy every LOCAL acceptance invariant.

## 8. Next gate

**Architecture review.** Stop here. MIXED and Stage 1 remain not run and unauthorized in this campaign.
