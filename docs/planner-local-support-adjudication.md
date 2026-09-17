# Retained LOCAL support adjudication

## Outcome

**RETAINED LOCAL ACCEPTED — HARNESS-ONLY acceptance defect.**

Production remains frozen at `23bce4bc842e598d19866d92d497772e1f3c0538`. The original [`schema5-governing-applicability-diagnostics-1:local` report](planner-governing-applicability-local.md) remains historically **stopped** and unmodified. Its immutable planner evidence satisfies the corrected acceptance contract. This adjudication made **zero provider calls, zero checkpoint writes, zero encrypted planning-record mutations and zero new reservations**. No LOCAL, MIXED or Stage 1 ran.

## First meaningful blocker

The original harness raised `DIAGNOSTIC_APPLICABILITY` because it required exactly one support. Production had already committed one required classifier with four qualified supports and four proven governing properties. Support cardinality is not operation cardinality; no privileged realizing sentence is required.

## Root cause

The harness conflated a single canonical effect with a single supporting span. Acceptance now requires **at least one positive support per required effect**, validates each support against its current contribution, basis, references, effect and ownership, and preserves all single-classifier, dataflow, necessity, governing-property and dependency checks. Frozen production validators remain responsible for current proof validity and conflicts.

The four retained supports were reviewed independently in their complete clauses; a common target alone was not accepted as semantic proof:

| Owned span | Exact text | Semantic review |
|---|---|---|
| `[29,57)` | classifying a single record. | The complete request asks for a workflow performing classification of one record; this span specifies that requested work. |
| `[325,345)` | Classify as rejected | The imperative requests production of the rejected classification under the attached false-approval condition. |
| `[370,374)` | high | In the complete classification clause, high inherits Classify as and requests the high result under the attached approval and threshold condition; it is not an isolated domain label. |
| `[420,432)` | and standard | The coordinated phrase inherits Classify as and requests the standard result under the separately attached otherwise fallback. |

The last three occur in: “Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.” The shortened branch spans inherit the clause's requested classification and retain their separately attached conditions/fallback. All four have current `requested_result_production` proofs with `ModelQualification` origin. All original qualification answers were replayed under their original schemas and match the persisted current proofs.

## Authority analysis

The admitted operation remains `operation_e226ff9ed819b5a645e7d9de`: one required `local_processing` result realization, **record + threshold → classifiedResult**, with an empty deterministic operation-producer set. Four supports materialize this one operation. There are no external, lifecycle or human operations.

The deterministic/local/in-memory description `[539,599)` remains governing-only despite its preliminary `external_execute` kind. The conditions and fallback are the other three governing properties. All four applicability proofs target the realized classifier. Preservation remains declaration/dataflow evidence. Preliminary `EvidenceRole` has no current execution authority; only validated contribution proofs authorize support.

Current contribution **v2**, applicability **v1**, coverage **v3**, effect **v7**, admission **v12** and dependency **v1** validate without modification. Completed-admission re-entry used a rejecting transport and writer: **zero calls, zero writes**, unchanged canonical IDs, dataflow, necessity, all proof fingerprints and complete snapshot.

| Integrity item | Fingerprint |
|---|---|
| Checkpoint | `0653605dfb1bca071c19dfcb490b9b9ee1b762c0ff859ad20a868ecff8b3b530` |
| Snapshot, before and after | `0f968cddbd4dc17cac2ba7997527f81384214dc84d24b8f52ba565b4e073c1eb` |
| Admission | `236359159d09176b25bd8b9570ed45147e53c863291912a5dec64d4977458f64` |
| Coverage | `c209fd2d6f1bea736cdebf8b097ef686409c3e252e31a54bf136c7bf89cb6ab6` |
| Dependency | `704e86c630f88531e82cfe6917cb6cd07138825a17e127f42eec5a4a0b754afd` |
| Budget | `eb6898d54b18213a533a5131ed8d16a3d88ed8e1be845756850f4ee8988decf8` |
| Archive values and timestamps, before and after | `c7a9208b9e07ecd47d3e4eceac736e058521d52a9df96adc89ecf3a6fa8be196` |

## Proposed action

The harness-only correction and restricted `adjudicate-local-support` command are implemented. The command accepts only the exact retained case, reads through public KeyVault APIs, forbids record mutation, checks original receipts/current proofs and emits a separate report. It creates no session, index, journal or budget. Public KeyVault reads retain their ordinary access-audit behavior; the archived planning records and accounting are fingerprint-identical.

The original stopped report and live evidence have not been relabelled or rewritten. The separate [machine-readable adjudication](planner-local-support-adjudication.json) records all four support reviews, original request/receipt fingerprints, applicability proofs, restart results and integrity checks.

## Validation performed

- Harness/tests built with production-reference rebuilding disabled: **zero warnings**. The isolated output uses byte-identical copies of the 22 frozen production DLLs. A missing-DLL staging error before entrypoint execution was corrected by copying those frozen artifacts, without rebuilding production.
- Focused harness checks: **116 passed**. Focused contribution/applicability/coverage/necessity/ordering/restart checks: **53 passed**.
- Full offline solution suite (`--no-build --no-restore -m:1`): **3,642 passed, 1 existing optional skip, zero failures across 29 projects**, including **1,064 planner tests**.
- Generic synthetic regressions cover one/multiple supports, one canonical operation, unqualified/property support, invalid bases, foreign targets/ownership, necessity conflicts, enumeration, genuinely different qualification page packing, partial qualification restart, receipt/accounting preservation and zero-call/write completed re-entry. Synthetic test packing variations do not change live configuration.
- Historical evidence: **12 original-schema receipts**, zero schema findings; original qualification answers reproduce current proofs. No synthetic answer was substituted into the retained checkpoint.
- All **15 recorded production sources, 22 tested DLLs, 2 published binaries, 4 packages, 2 fixture sources, the original harness DLL and both original report files** remain unchanged. The entire production source tree also matches the frozen commit. Retained build/package/publish evidence was hash-verified; those production builds and publishes were not repeated for this harness-only task.

## Decision accounting

This task introduced **zero planner/model decisions and zero provider calls**. Read-only receipt replay did not add usage or consume allowance. Historical live accounting remains **12 calls**, **30,132 input / 3,777 output / 1,930 reasoning tokens**, with **4 of 16 calls remaining**. The retained run had zero partitions, escalations, semantic repairs or unverifiable dispatches.

| Historical phase | Decisions | Verified calls |
|---|---:|---:|
| Interpretation | 8 | 8 |
| Contribution qualification | 3 | 2 |
| Realization coverage | 1 | 1 |
| Governing applicability | 4 | 1 |
| Boundary scope / occurrence identity / operation dependency | 0 | 0 |

These are retained live measurements, not new dispatches or provider-call savings. Four supporting and four governing contributions are evidence outcomes, not eight additional provider calls. Detailed proof and request fingerprints are in the JSON report.

## Next gate

Recommend **one fresh MIXED**, requiring separate authorization. MIXED and Stage 1 remain **not run**. This acceptance establishes retained LOCAL admission convergence only.
