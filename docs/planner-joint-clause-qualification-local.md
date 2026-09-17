# Joint clause qualification — fresh LOCAL

## 1. Outcome

**PROVIDER BLOCKER — category A.** Exactly one fresh LOCAL was started under `schema5-joint-clause-qualification-diagnostics-1:local`, with production frozen at **`d2ceac7cd85793baea732721f4e2b092c94be210`**. It stopped on the first interpretation request. Canonical qualification and admission were not reached; this run supplies no new semantic evidence about joint clause qualification.

Production was not changed. Harness-only changes set the campaign/comparison identities and authorized commit, report clause units and request links, and validate full provenance membership rather than the diagnostic scalar parent. MIXED and Stage 1 were **not run**. Detailed redacted evidence is in [the JSON report](planner-joint-clause-qualification-local.json).

## 2. First meaningful blocker

- Failure: `LLMClientException`, phase `intent`, location `$`, unverifiable.
- Decision: `interpret_40997e14a2c6d889e553c1a9`.
- Page: `page_7af6ccd48db99329ff307e7afae91070eeb8945709c860bab473fce2df15fba9`.
- Exact request fingerprint: `358686e7a0733e9c79b11495da02d4ed79be9212080e6ea7431f1ab202d9acee`.
- Original schema fingerprint: `1218a91ea3b0c4707641fee38135182fe80fe3aa6bb12fbd5e6d0044c89e39e9`.

One coordinator reservation, one durable budget reservation and one journaled request exist. There is **no verified receipt**. The request used the normal **8,192** output ceiling and `low` reasoning. No provider or campaign retry occurred.

## 3. Root cause

The evidence establishes a provider/transport exception without a verifiable response. The underlying HTTP or infrastructure cause is not established by the redacted report. The exception details, request and original schema remain in encrypted audit storage. There is no evidence of output exhaustion or a failed semantic planner invariant.

Actual input, output, reasoning and final-answer token usage are **unknown**. The raw report's zero token counters are verified-receipt subtotals, not proof of zero provider consumption.

## 4. Authority analysis

The coordinator reserved before dispatch, preserved the missing receipt and stopped. Missing provider evidence grants no authority to modify qualification, domains, identities, declarations, budgets or validation.

The typed host-policy metadata and port-only baseline remain unchanged inputs. Their interpretation domains contain zero host-policy and zero structural-baseline model decisions, but the run stopped before projection/admission completion. Empty result counts cannot establish convergence.

No operation, contribution, coverage, applicability or dependency proof committed. Completed-admission restart acceptance was therefore **not reached**. A detached audit of the stopped checkpoint made zero calls and replayed zero receipts; it stopped at `INTENT_OPERATION_PROOF_MISSING`, as expected for an incomplete admission.

## 5. Proposed action

Preserve this stopped case and its unverifiable reservation. Do not resume or redispatch it. No production patch, replacement LOCAL, MIXED, Stage 1 or isolated provider request is included in this iteration.

The next gate is a **separately authorized isolated diagnostic** of the retained transport failure/request. Missing usage must remain unknown; no limit or reasoning increase is justified.

## 6. Validation performed

The recorded **17 production-source hashes**, **22 production DLL hashes** and declaration fixtures match before and after execution. The separately frozen harness hash is recorded in the manifest. All historical report hashes remained unchanged.

The prior offline report retained 3,689 passing tests and one optional skip. This campaign reran the solution without rebuilding production: **3,690 passed, one failed, one optional provider-test skipped**, across 29 assemblies. The single failure was the telemetry test `RecoveryTelemetryReportsRepairOutcome_WithoutPrivateContentOrFinalFailure`, which observed persisted stopped status before a completed tracing span. The source saves status before disposing the activity, consistent with a test timing race. The unchanged isolated test passed, then the entire Agent project passed **546/546**. All **1,092 planner tests** passed. The failed initial run is preserved rather than reported as a clean first pass.

The **142 focused harness checks** passed, including LOCAL-only authorization, refusal of second starts/MIXED/Stage 1, stale proofs, support/provenance validation and receipt-accounting safeguards. Fixture selfchecks passed for both synthetic cases with zero-call/zero-write restart. Six classifier/batch reference cases and eighteen frozen reference fixtures passed without provider/business transport.

Prior `/tmp` publish artifacts and logs were no longer present. Both frontend builds, four package checks, Native AOT planning/encrypted-restart and trimmed Agent.Server persistence publication/smokes were regenerated from the frozen source and passed. Harness/tests were built with production-reference rebuilding disabled. The diagnostic's production DLLs remained unchanged. Builds/publishes were warning-free under the existing documented exceptions; no suppression was added.

The existing .NET exchange-rate provider checked **USD → EUR once**, returning a valid quote dated **2026-09-16**. No quote was injected and no model probe was made. Freeze verified unchanged `gpt-5.5-2026-04-24`, all-low profiles, sixteen durable reservations, 12,000 input ceiling, 9,600 dispatch target, normal 8,192 output, bounded 16,384 singleton escalation, fixtures, typed policy and catalog.

All **16 historical LOCAL answers** still validate under their original schemas, with zero findings. Current-proof audit reused no receipts and dispatched nothing. The historical checkpoint remains `b435e8aafe04f22f81db5afefdac4e50bf924b46f518188bf3613c474b4d4c58`; its accounting and stopped status are unchanged.

The new stopped checkpoint is `414c8938999a5dd3598655c790307c7b5d852e4b80a3c42ba744726d10ddf03e`. Reloaded reporting is identical. The archive aggregate was `8146421b786b9c39875adb4139ff0220d75e63d6f58c5e1cfdc93b2e56e65114` before LOCAL and remained unchanged afterward.

## 7. Decision accounting

| Phase / decision class | Decisions exposed | Journal requests | Verified calls |
|---|---:|---:|---:|
| Interpretation | 1 of 8 planned | 1 | 0 |
| Joint clause qualification | 0 | 0 | 0 |
| Realization coverage | 0 | 0 | 0 |
| Governing applicability | 0 | 0 | 0 |
| Occurrence boundary / scope | 0 | 0 | 0 |
| Occurrence identity | 0 | 0 | 0 |
| Operation dependencies | 0 | 0 | 0 |

Interpretation preflight had **8 decisions on 8 initial pages**. Only the first was exposed. Runtime facets are embedded in interpretation, not additional calls. Typed host/baseline projection model decisions were zero by construction; their persisted engine projection counts are zero because interpretation did not complete.

- Verified provider receipts: **0**; unverifiable requests: **1**.
- Durable allowance consumed: **1/16**, remaining **15**. Unverifiable usage prevents continuation regardless of that numerical headroom.
- Largest estimated input: **5,549 tokens**. Actual input and all output/reasoning usage: **unknown**.
- Partitions: **0**; escalations: **0**; semantic repair pages/allowances: **0**.
- Canonical operations/dataflow: **not established**. Supporting units, governing attachments and deterministic canonical assignments: **not reached**.
- No current admission fingerprints or completed-admission restart result exist.

No planner decisions were added or removed by this harness gate. Previous synthetic packing remains offline evidence only; this early stop proves neither call savings nor admission convergence.

## 8. Next gate

**Isolated diagnostic — requiring separate authorization.** Stop here. No further live execution.
