# Canonical execution contributions — LOCAL 1

## Outcome

**VERIFIED BLOCKER — category C. LOCAL did not pass.**

Exactly one fresh LOCAL ran as `schema5-execution-contributions-diagnostics-1:local` on frozen production `5617c1b9024b48f09c0baf49d5e6f146df130879`. Production code, model, low reasoning, budgets, declaration fixture and all proof logic remained unchanged. MIXED and Stage 1 were not run. No replacement, retry or isolated provider experiment followed the blocker.

One required `local_processing` operation committed: `operation_e226ff9ed819b5a645e7d9de`, with `record + threshold → classifiedResult`. Coverage v2 has one positively qualified supporting contribution and two governing contributions. Admission v11, contribution v1, effect v6 and dependency v1 validate. The deterministic/local/in-memory description was excluded, so the complete LOCAL acceptance criteria failed.

## First meaningful blocker

The harness stopped with **`DIAGNOSTIC_DESCRIPTIVE_EVIDENCE`**, phase `intent_operations`, after atomic admission. The reported stop location is `$`; the affected operation is `operation_e226ff9ed819b5a645e7d9de` and the missing governing reference is `r_de56e7ee8e0c77fcc82d8715` (request coordinates `[539,599)`).

The first erroneous semantic classification was verified request **2**, decision `interpret_41e009d84001c346e10c5cea`. It classified the description as semantic `implementation_policy` and runtime `external_execute`, with no occurrence boundary. This answer satisfied its original schema.

Qualification decision `contribution_runtime_a12de0079dc30cbf0c9de06e`, packed into request **10**, returned `excluded / no_requested_execution`. Its original schema contained **no effect target and no supports/governs alternative**—only a qualified exclusion or `unresolved`. Coverage therefore had no qualified governing contribution for the description.

| Evidence | Request fingerprint | Receipt fingerprint |
|---|---|---|
| Interpretation, request 2 | `8d0200b76ead2d3c13e7fca9c49c32ad871e0939111158c272f3ee872e675b5d` | `e462a4dd43219b7aaa773166c456df25a2bc0b41561585254d869583e844f68a` |
| Qualification, request 10 | `61638fbb5be57548ee1541880737e422c7922485aa414a40581a8eeb20bc3e38` | `fbe0d1902ecde314c39c12697e0abd4e0cbc0fff35cb5cd42162a6f57b06e0f0` |

The complete request identities and original schemas are retained in the [machine report](planner-execution-contributions-local-1.json) and encrypted journal.

## Root cause

Contribution qualification successfully prevented raw runtime evidence from becoming executable support. However, governing eligibility still inherited the preliminary execution kind. `EffectDomain` offers the public result realization to `local_processing`; compatibility with invocation evidence also requires the same kind/role. The description's preliminary `external_execute` classification therefore left its effect domain empty.

Absence of requested external execution then became an accepted exclusion. This removed the description's governing applicability along with its execution authority. No external operation was invented, and coverage did not promote a property into support. The failure is lost governing evidence across the preliminary-kind → canonical-contribution boundary.

The handled output truncation is separate: request 8 exhausted 8,192 output tokens; its authorized singleton escalation completed at 16,384. It was a contract-interpretation decision, not the description's interpretation. All twelve dispatched requests have verified receipts. There was no provider/unverifiable blocker or exhausted call budget.

## Authority analysis

The engine correctly owns canonical effects, declaration exclusions, occurrence boundaries, coverage qualification checks and the empty singleton dependency proof. Only a current contribution proof authorized the single supporting span; every preliminary/historical `EvidenceRole` was null in this fresh runtime evidence. The offline regressions also verify historical `action` labels have no support authority.

The model owned the erroneous preliminary kind. At qualification, the engine knew that no external occurrence had been proved and correctly prevented external realization. It did not have structural proof permitting automatic attachment to an arbitrary local effect. Nevertheless, the issued domain offered retirement without preserving the governing question. A preliminary kind still controls whether canonical property qualification can reach the correct effect.

This recurring governing-authority failure warrants **architecture review**. Do not infer a keyword veto, merge effects, reinterpret the answer, weaken external safety or add another retry. Governing applicability and requested execution authority need review together before any production change.

## Proposed action

**No production change in this gate.** Review the ownership of governing target eligibility and the meaning of `no_requested_execution` exclusion. Preserve the captured schema and answer as immutable evidence. Do not advance to MIXED.

## Validation performed

**Offline prerequisites rerun:** 3,601 tests passed across 29 solution assemblies, including 1,049 planner tests; one optional provider test was skipped. Focused harness tests: 149 passed. Six harness regressions were added: complete governing-only property coverage (including rejection when also marked support, missing or partial), and multiple legitimate support spans on one effect. Harness/test builds were warning-free and did not rebuild production references.

Published Native AOT planning/encrypted-restart and trimmed Agent.Server persistence smokes passed. Reference selfchecks passed six classifier/batch cases and eighteen additional frozen cases. Synthetic harness selfchecks passed with no provider calls, including restart checks; they are not live MIXED evidence. Existing production build, frontend, four package and publish evidence was retained only after hash verification. The previously retained solution result was 3,595 passes and one skip.

Currency preflight checked USD → EUR once, obtaining a quote dated 2026-09-16. No quote was injected and no provider probe was made. Frozen configuration and declaration-fixture comparisons passed.

**Historical replay:** all nine prior LOCAL receipts validated under their original schemas. Current code refused historical admission authority with `INTENT_OPERATION_PROOF_MISSING`. Zero provider calls; archived checkpoint and budget unchanged. No historical answer was imported into this fresh case.

**Fresh live evidence:** eight interpretation decisions used nine calls; three qualification decisions packed into two calls; one coverage decision used one call. The supplied fixture retained required `record`, optional non-nullable `threshold` with omission default `100`, and required `classifiedResult`. Preservation was excluded from operation admission through canonical output coverage. Host policy projected eight engine-owned clauses into nine obligations; five baseline structural units remained engine-owned. No host/baseline model interpretation decision and no unresolved runtime scope occurred.

**Post-stop read-only verification:** all eleven completed answers satisfy their original schemas; the remaining receipt is the verified output-limit result without an answer. Detached deserialization and completed-admission re-entry validated contribution **1**, coverage **2**, effect **6**, admission **11** and dependency **1** with **zero provider calls and zero checkpoint writes**. IDs, contribution/coverage/dependency/admission fingerprints and the complete snapshot were identical. This validates persistence of the committed proof, not the missing semantic attachment. The original stopped campaign/report was not resumed or altered.

| Current proof | Fingerprint |
|---|---|
| Complete post-stop snapshot | `eca6f160b9604aed70cd980dcf1440cff170d1c188846155a41956f453f61b73` |
| Admission-set fingerprint | `ad1464c5671e6c824ebf4d081c86d5f9417c15cce467203395a8527df93c9ce2` |
| Operation admission v11 | `8758731c16ab31dc8cc24d5e3d34d0f8d67846c5627e3d05182f85794e954aba` |
| Coverage v2 | `362b9722b2bb94a309b25ddb210b2d864253654b26f2cd0bb7b112d63debc366` |
| Dependency v1 | `a35fd448b17b7fab3a41df2f5acb1a69cf9d43ca87090d0fd23252755e68b995` |

All 13 recorded production source hashes, 22 production DLLs, the new harness hash, four packages, fixture sources and both published binaries still match their frozen values. Archived accounting remained unchanged. Harness SHA-256: `eb57600e26d485d8547868ad284db556de66abb20b4dbbbf3faf191f0ef090cd`.

## Decision accounting

| Decision class | Distinct model decisions | Verified provider calls |
|---|---:|---:|
| Interpretation | 8 | 9 |
| Canonical contribution qualification | 3 | 2 |
| Realization coverage | 1 | 1 |
| Separate governing applicability | 0 | 0 |
| Occurrence boundary/scope | 0 | 0 |
| Standalone occurrence identity | 0 | 0 |
| Operation dependencies | 0 | 0 |
| **Total** | **12** | **12** |

Interpretation had **8 initial pages** and **8 embedded runtime-facet decisions**; runtime facets caused no separate calls. Runtime interpretation yielded **22 evidence records**. Deterministic work included **19 contribution exclusions**, **one canonical operation assignment**, **two governing attachments**, and the empty operation-producer proof. These are separate measures, not saved-call counts. Qualification yielded one support, two governing contributions and one semantic exclusion. There were no new planner decisions introduced by this harness gate.

| Usage and limits | Result |
|---|---:|
| Coordinator reservations / journal requests / durable calls | 12 / 12 / 12 |
| Verified input tokens | 30,908 |
| Verified output tokens | 11,914 |
| Reasoning tokens, included in output | 10,657 |
| Final-answer tokens | 1,257 |
| Unknown usage / unverifiable requests | 0 / 0 |
| Output partitions | 0 |
| Bounded singleton escalations | 1, completed |
| Semantic repairs | 0 |
| Largest estimated / actual input | 5,549 / 3,999 |
| Remaining durable call allowance | 4 of 16 |

There is no provider-call savings claim. Twelve unique semantic decisions and twelve calls coincide here because qualification packing and one interpretation escalation offset each other. The budget was not exhausted.

Every qualified contribution is listed below. Coordinates are half-open source spans. `classifier` means `operation_e226ff9ed819b5a645e7d9de`. A basis on an excluded or governing record is not executable support authority. Baseline and host payloads remain redacted; exact contribution IDs, parent IDs, domain/proof fingerprints and source references are in the machine report.

| Owned reference | Source coordinates | Qualified role | Basis | Canonical effect | Origin |
|---|---|---|---|---|---|
| `r_8197e8af84cbe55b9a300e2f` | `request[325:432]` | governs | `governing_property` | classifier | ModelQualification |
| `r_eb43881e9070e8f80a5992f5` | `host[0:46]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_bd356071a53097299594669b` | `request[58:153]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_1a414791d972a04a90acfa76` | `baseline_source_c0a24f04dff28138d5ca76f7[0:877]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_8a252984f13aebb6308dc847` | `request[0:57]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_8ef413f888e88d2a4ff3cc01` | `host[332:396]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_46a6200df250f974b587de2d` | `host[396:536]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_61f3b39269b19a8d1b2c3206` | `host[46:133]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_65a80ea27a45bc272f44c35f` | `host[536:603]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_ffccc8b701a0b936592e0c25` | `baseline_source_cfcce8adaeb2efbe14de1c90[0:1017]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_9df2657bc7acb47d57d15673` | `request[444:501]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_c966b845a674235e9909592e` | `host[207:261]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_73cd12f99e80be89eb2592a2` | `host[261:332]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_45da95251fac200944e135b8` | `baseline_source_8a2b72b4f8b89845348afa27[0:110]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_de56e7ee8e0c77fcc82d8715` | `request[539:599]` | excluded | `no_requested_execution` | — | ModelQualification |
| `r_67792b2d03166bca8227d78e` | `request[236:324]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_115f81c9e049985089243a4e` | `host[133:207]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_8c89d7f0f8222c188449ae0b` | `request[41:57]` | governs | `governing_property` | classifier | ModelQualification |
| `r_bf2fcba8f6ec66941dd78433` | `request[29:40]` | supports | `requested_result_production` | classifier | ModelQualification |
| `r_738d21fa5834cf78fb5ba9b4` | `baseline_source_6cd05a6de9e2f265221ab961[0:345]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_05aa4b457625655d33f19154` | `baseline_source_08800f32e057eb8460544d94[0:43]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_4612eeffe081bb751d860b86` | `request[502:538]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |
| `r_a4cacabb6a0c4bba1b3fded6` | `request[154:235]` | excluded | `canonical_source_or_declaration_exclusion` | — | DeterministicExclusion |

The only support basis is `requested_result_production`, bound to canonical output `decl_e10c86997aef941cbd55d2f1`. The rules/fallback and the short action's object description govern the classifier. The deterministic/local/in-memory statement is the model-qualified exclusion at `[539,599)`; it appears in no executable support or governing assignment.

## Next gate

**Architecture review.** LOCAL is stopped. MIXED and Stage 1 remain **not run**.
