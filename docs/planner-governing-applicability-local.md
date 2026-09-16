# Governing applicability v1 — fresh LOCAL

## Outcome

**LOCAL STOP — harness support-count mismatch. Canonical admission converged.**

One fresh `schema5-governing-applicability-diagnostics-1:local` ran against frozen production `23bce4bc842e598d19866d92d497772e1f3c0538`. The original encrypted report remains `stopped / DIAGNOSTIC_APPLICABILITY`; it has not been rewritten as a pass. [Machine-readable evidence](planner-governing-applicability-local.json) preserves the report, exact request/receipt fingerprints, separate read-only verification, and validation hashes.

No production code changed. No replacement LOCAL, MIXED, Stage 1, provider retry or isolated provider request ran. All model settings, budgets, paging and output handling remained unchanged.

## First meaningful blocker

The harness's `RequireLocalApplicability` assertion rejected **four supporting contributions instead of exactly one**, after production had atomically committed admission. Location: harness acceptance, reported as `$` in `intent_operations`.

I interpreted “one positively qualified executable-support contribution” as an exact count when preparing this harness. The approved architecture also permits several legitimately qualified execution spans for one effect. The count mismatch therefore does **not** establish a planner semantic failure. A clarification was requested; the frozen result and evidence remain unchanged. There is no basis here to classify a provider failure, output exhaustion or dependency/applicability architecture defect.

## Root cause

The live model qualified the short classification request and three result-selection branches as support. It qualified the three associated conditions/fallback and the deterministic/local description as properties. This differs from the synthetic one-support/three-property decomposition while retaining one canonical effect.

Canonical operation: `operation_e226ff9ed819b5a645e7d9de`, required `local_processing`, `main` result realization:

`record + threshold -> classifiedResult`

There are zero external, lifecycle or human operations, zero unresolved runtime scopes, and no upstream operation producer. Preservation remains attached to the output declaration. The short classification/object span remains qualified execution evidence; it was not separately decomposed into an object property in this live answer.

All support contributions target the same canonical result:

| Contribution | Owned reference / half-open span | Positive basis | Origin |
|---|---|---|---|
| `contribution_6bfa7fe3acbe80a73bfec7fb` | `r_9aad398c80e8779264f62a9b` [29,57) | `requested_result_production` | `ModelQualification` |
| `contribution_2d792ef998f25a75e0317558` | `r_28eb81c34184f7a2b9246e9f` [325,345) | `requested_result_production` | `ModelQualification` |
| `contribution_633683855b4b341bc5b1e558` | `r_d2c83026765129e3ce5ede1b` [420,432) | `requested_result_production` | `ModelQualification` |
| `contribution_75ec6da01123a2dbaa49c376` | `r_c58ef82ab6709b3557eb348b` [370,374) | `requested_result_production` | `ModelQualification` |

Every governing property targets `operation_e226ff9ed819b5a645e7d9de`:

| Contribution | Owned reference / half-open span | Preliminary kind | Applicability origin | Applicability proof fingerprint |
|---|---|---|---|---|
| `contribution_494617229a94a7b7f2288aa2` | `r_360ed5a0fa22353547c9eb0f` [539,599) | `external_execute` | `ModelApplicability` | `1412d772eac9d2d874e652cd2a1d1d00f8ee100a3e75ae283a58eb2d47ea17f4` |
| `contribution_9e142611de2e8da0c6ba6cd2` | `r_46cfd1b253bd51d50aea2701` [433,443) | `local_processing` | `ModelApplicability` | `b0a3e5cc7e22d7f6c8fe56ad8b28a1a557108ffe26148900d1c1720f033facca` |
| `contribution_e4a489034bfce2f1e31de9ca` | `r_d5f6b000075aca2e351478e4` [375,419) | `local_processing` | `ModelApplicability` | `69284e08e63a1b06e8d6d3152b30f27b1257cd9e6eb1b3fa2fa6b9edf08eafc5` |
| `contribution_f01a003b50058993197e3a35` | `r_3e4f383d5cc4e976bc018b99` [346,369) | `local_processing` | `ModelApplicability` | `06e896ba791711235761c35b164b896e892ce1d65c608975d26b7ca4a055db31` |

The property at `[539,599)` retained **`external_execute`** as its preliminary kind. It received unbound property qualification, then model-proven applicability to the realized local effect. It contributed **zero executable support** and created no external operation. This directly exercises the requested wrong-kind regression. Conditions and fallback retain their complete classification-clause context.

## Authority analysis

Production contribution v2 owns executable qualification; coverage v3 consumes it. Effect v7 and admission v12 validate the resulting single operation. Applicability v1 owns property targeting after realization. The preliminary runtime kind and historical `EvidenceRole` have no attachment or support authority.

Read-only checks verified qualification requests 9–10 preceded coverage request 11, which preceded applicability request 12. Applicability domains exposed only the realized canonical effect. All four properties have `ModelApplicability` proofs: singleton cardinality was not treated as deterministic semantic proof. Their four attachment projections are deterministic after those decisions.

Dependency v1 establishes an empty producer set, with zero dependency-model decisions. `Effect.Producers` remains empty. No dependency or occurrence identity was inferred from the properties.

The failed exact-support-count assertion belongs to the harness. It must not be used to justify a production patch or to require one privileged realizing sentence. No production safety validation was weakened or bypassed.

## Proposed action

Stop with the single run preserved. Resolve whether this gate's support criterion means “at least one qualified support” or “exactly one” using the captured evidence. No further provider call is needed to establish the present proof state. No production fix is proposed from this count discrepancy.

The campaign's original stop remains durable. A separate read-only inspection validated the already committed proof; it did not resume the campaign, change its acceptance, write its checkpoint, or consume another reservation.

## Validation performed

**Prerequisites rerun:** 3,621 passing solution tests across 29 assemblies, one optional provider-test skip; planner component within that run: 1,062 passed; focused harness: 156 passed. Reference checks passed 6 classifier/batch cases and 18 frozen fixture cases. Synthetic harness selfchecks passed without provider calls. Published Native AOT planning/encrypted restart and trimmed Agent.Server persistence smokes passed. Existing production build, frontend and four-package evidence was hash-verified and retained.

Harness builds disabled production-reference rebuilding. One parallel MSBuild worker failure was followed by a clean serial build. A pre-dispatch chronology assertion initially included historical pages during synthetic replay; it was confined to fresh campaign chronology, and corrected synthetic acceptance passed. Final builds emitted zero warnings/errors. These were harness preparation issues, before any live call.

**Exchange rate:** one existing-provider USD→EUR check passed with quote date 2026-09-16. No quote injection, monetary override or model probe occurred.

**Historical replay:** prior LOCAL's original schemas and archived checkpoint/accounting remained unchanged. Current-proof replay stopped at `INTENT_OPERATION_PROOF_MISSING`, with zero provider dispatches and no substitution of synthetic evidence.

**Synthetic evidence:** detached corrected fixture passed one support/three properties and zero-call/zero-write restart. It remains explicitly separate from this live four-support/four-property result.

**Live evidence:** all twelve returned assignments validate against their original schemas, with zero schema findings. Admission committed before the harness assertion. Production and all frozen harness, fixture, package and published-binary hashes were rechecked unchanged. Archive accounting fingerprint remains `22f0b0985f76aac4c21daec76239dd33539132463de8196c6320e7ee9528931e`.

**Read-only persisted re-entry:** contribution 2, applicability 1, coverage 3, effect 7, admission 12 and dependency 1 validate. The frozen harness's rejecting transport and rejecting checkpoint writer recorded **zero provider calls and zero writes**. Canonical IDs, all contribution/applicability/coverage/dependency/admission fingerprints, accounting and the complete snapshot were unchanged. The description's governing-only coverage and declaration preservation were independently checked after the original count assertion had stopped the normal acceptance path.

- Persisted checkpoint: `0653605dfb1bca071c19dfcb490b9b9ee1b762c0ff859ad20a868ecff8b3b530`.
- Snapshot: `0f968cddbd4dc17cac2ba7997527f81384214dc84d24b8f52ba565b4e073c1eb`.
- Overall admission: `236359159d09176b25bd8b9570ed45147e53c863291912a5dec64d4977458f64`.
- Operation admission: `d5375b2a6099f7a715cf577d3ccc66ff195d6dd975d207d93a36ae23d620bd20`.
- Coverage: `c209fd2d6f1bea736cdebf8b097ef686409c3e252e31a54bf136c7bf89cb6ab6`.
- Dependency: `704e86c630f88531e82cfe6917cb6cd07138825a17e127f42eec5a4a0b754afd`.

The original report's `readOnlyRestart` remains absent because its acceptance assertion ran first. The separate inspection result is preserved alongside it, rather than backfilled into the archive.

## Decision accounting

| Class | Logical decisions | Verified calls | Input tokens | Output tokens | Reasoning tokens |
|---|---:|---:|---:|---:|---:|
| interpretation | 8 | 8 | 24,744 | 1,791 | 958 |
| execution_contribution | 3 | 2 | 2,651 | 1,218 | 692 |
| realization_coverage | 1 | 1 | 1,312 | 162 | 37 |
| governing_applicability | 4 | 1 | 1,425 | 606 | 243 |
| boundary_scope | 0 | 0 | 0 | 0 | 0 |
| occurrence_identity | 0 | 0 | 0 | 0 | 0 |
| operation_dependencies | 0 | 0 | 0 | 0 | 0 |
| **Total** | **16** | **12** | **30,132** | **3,777** | **1,930** |

Eight interpretation decisions occupied eight initial pages. Their eight runtime facets are embedded in those same calls, not additional calls. Three contribution decisions packed into two calls. One coverage decision used one call. Four applicability decisions packed into one call. Actual initial pages/calls: twelve, with no extra requests. The extra logical property decision relative to the synthetic fifteen-decision estimate did not add a provider call.

Engine-owned facts: eight typed host clauses projecting nine obligations, five baseline structural units, nineteen deterministic contribution exclusions, one canonical identity, four attachment projections, and one empty dependency proof. Host and structural baseline interpretation decisions: zero. Preservation contributed one declaration-covered occurrence exclusion. Four support assignments derive from qualified evidence; they are not four operations.

Twelve coordinator reservations, twelve journal requests, twelve verified receipts, and twelve durable budget charges agree. Unverifiable requests, missing usage, partitions, singleton escalations and semantic repairs: **zero**. Known non-reasoning output: 1,847 tokens. Largest estimated input: 5,549; largest actual input: 4,007. Remaining durable call allowance: **4 of 16**. Nothing remained queued for provider work at the harness stop.

No saved-call claim is inferred from synthetic envelopes, removed fields or engine projections. No behavior review, construction, execution case or Stage-1 success is claimed.

## Next gate

**Acceptance-cardinality clarification on the retained LOCAL evidence.** A production architecture review is not justified by the support count alone. MIXED remains **not run** and requires separate authorization; Stage 1 remains **not run**.
