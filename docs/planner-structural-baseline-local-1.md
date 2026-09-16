# Typed baseline projection — one LOCAL diagnostic

## 1. Outcome

**VERIFIED BLOCKER: `LLM_BUDGET_EXCEEDED`; canonical admission incomplete.** Campaign `schema5-structural-baseline-diagnostics-1` started LOCAL exactly once on production `ba4f657610740207579ed5c2e14f99abaaa82e0a`. MIXED and Stage 1 were **not run**.

Typed baseline projection passed its live boundary: five engine-owned structural contracts, zero baseline interpretation decisions, zero invented baseline operations and zero unresolved runtime scopes. This is partial evidence, not LOCAL or Stage-1 success. Production sources, all 22 deployed production DLLs, fixtures and archived accounting remained unchanged.

Detailed redacted requests, lineage, hashes and evidence are in [the machine-readable report](planner-structural-baseline-local-1.json).

## 2. First meaningful blocker

The sixteen-call allowance was exhausted before dispatching occurrence-identity decision `operation_runtime_a4ef97881392bffaafe7cfa2` in `intent_operations`, gate `response_contract`. The recorded location is `$`; the captured stack identifies `LLMUsageBudgetScope.EnsurePreCallLimits`, line 349, checking `MaxCalls`.

- Request fingerprint: `0111453562a9280e310ff4733cd94dc4dbf949b4cd7ed8ace49f8bbb4f85d67a`.
- Schema fingerprint: `a6c2bb2fb92946fd301b7780f50b5b7fc18d8c5f8c5ff708433d6c18d68f0e51`.
- Receipt: **absent**; request 17 did not reach transport.
- Durable budget: **16 calls, 61,499 total tokens**.

The coordinator and journal contain seventeen request/reservation records. The journal persists a request before budget admission, so the rejected seventeenth entry is conservatively labelled `unverifiable`. That stored flag remains untouched. The captured exception and frozen call path prove a pre-transport budget rejection, not a provider-unavailable request. No receipt or usage is fabricated for it.

## 3. Root cause

Nine initial interpretation pages required fifteen provider calls because three multi-decision pages exhausted 8,192 output tokens and each produced two partition children. All three exhausted outputs were reported entirely as reasoning. Those partitions completed within the existing rules; they consumed no semantic repairs. The sixteenth call completed three realization decisions. A further identity selection needed another call, which the fixed budget correctly refused.

The verified realization answer `effect_runtime_a4ef97881392bffaafe7cfa2` selected three `main` invocation candidates while consuming the canonical `record` and `threshold` and producing `classifiedResult`. The issued domain also contained a `main` result-realization candidate, which was not selected. Two other contributions deferred to governing. The three invocation alternatives caused the pending identity request; **they are candidates, not three committed operations**.

Protocol category B applies to the observed output/reasoning exhaustion contributing to the budget stop. The immediate terminal condition is the diagnostic call ceiling. The queued identity domain is a separate unresolved convergence concern: zero identity calls does not prove deterministic identity when one such request was prepared but blocked. No governing or dependency assessment completed, and no structural-authority or dependency-authority regression is established by this run.

## 4. Authority analysis

The engine supplied all five baseline structural units and all nine host-policy runtime scopes. It validated scope ownership and declaration coverage, and enforced the unchanged call ceiling. Three preliminary runtime contributions were excluded through canonical declaration coverage, retaining omission/constraint/preservation evidence without operation authority.

The model still selected among possible effect anchors. One response retained three invocation identities, so the engine followed its bounded multi-identity path. This task does not decide that those candidates should be merged or filtered. The source of their ambiguity and the expectation of zero identity decisions require review before another live iteration.

The declaration fixture remains explicitly supplied: required `record`, optional non-nullable `threshold` with omission default `100`, required `classifiedResult`, and its enum/preservation attachments. Its proof fingerprint is `1410236caa12114fa5342d5449c666793f43a46e0349f396d9c4a2e32c29eb3b`. Fresh declaration convergence is not claimed. Atomic operation admission did not commit: canonical IDs, resolved necessity, final governing attachments and dependency proof are **unavailable**, rather than inferred from intermediate answers.

## 5. Proposed action

**No production change or automatic retry.** Review the retained realization domain and its three invocation alternatives, together with the measured request consumption, before choosing another gate. Do not increase the sixteen-call allowance, reasoning or output limits automatically. Do not resume request 17, launch a replacement LOCAL, or start MIXED.

## 6. Validation performed

**Offline rerun:** 3,514 solution tests passed, including 1,008 planner tests; one optional live-provider test skipped. The five added harness cases validate the structural baseline acceptance boundary. All 114 focused harness/persistence tests passed, including single-start and LOCAL-only guards. Synthetic fixture selfchecks and reference execution selfchecks passed. Existing published Native AOT/encrypted-restart and trimmed persistence smokes passed. Harness/test builds had zero warnings; production references were not rebuilt. The prior frontend, package and production build evidence was hash-verified and retained.

**Historical replay:** all eleven prior receipts remained valid under their original schemas. Changed-request replay stopped at `REPLAY_EVIDENCE_REQUIRED`, with zero provider dispatches and unchanged archives. The earlier equivalent source checkpoint counted 22 interpretation decisions; typed projection reduces it to 17. The historical offline packing comparison reported ten current pages; this fresh identity's preflight reported nine. These are distinct measured contexts, not evidence of saved live calls.

**Synthetic evidence:** fixture selfchecks are labelled synthetic and contain no provider calls. Their answers were not imported into LOCAL. Only the unchanged canonical declaration ports and attachments were installed after fresh interpretation.

**Isolated live evidence:** exactly one LOCAL, sixteen verified calls. The USD→EUR prerequisite checked once and returned a valid quote dated 2026-09-15; no quote was injected. Interpretation completed, baseline acceptance passed and realization grounding returned a schema-valid answer. The thirteen completed JSON responses all satisfy their original schemas; the remaining three receipts are output-limit responses without candidates.

**After-run audit/replay:** read-only public-KeyVault audit confirmed sixteen budgeted calls and unchanged checkpoint, budget and older archives. Completed-page replay stopped at `LLM_BUDGET_UNVERIFIABLE` for the missing seventeenth receipt, reused no provider receipts and dispatched nothing. This replay stop does not replace the original budget diagnosis. An isolated audit utility initially needed its native SQLite dependency copied into its own output directory; this involved no production changes or model requests. Final audit build was warning-free.

## 7. Decision accounting

| Decision class | Distinct answered decisions | Verified calls | Input | Output | Reasoning |
|---|---:|---:|---:|---:|---:|
| Interpretation | 17 | 15 | 28,613 | 28,144 | 26,744 |
| Realization grounding | 3 | 1 | 4,437 | 305 | 0 |
| Governing grounding | 0 — not reached | 0 | 0 | 0 | 0 |
| Occurrence identity | 0 answered; 1 queued | 0 | unavailable | unavailable | unavailable |
| Admission dependencies | 0 — not reached | 0 | 0 | 0 | 0 |

Total verified usage: **33,050 input, 28,449 output, 26,744 reasoning**; **1,705 final-answer tokens**, derived from compatible provider output/reasoning counters. All sixteen transported requests have usage. Request 17 has no provider usage/receipt; archived accounting remains unknown for that entry while the audit establishes it was blocked before transport.

There were **three truncations, six output-partition children, zero singleton escalations and zero semantic repairs**. The largest estimated input was **4,929**; largest actual input **4,437**. All issued requests retained `low` reasoning, an 8,192 output allowance and disabled transport retries. Existing 16,384 escalation was available but unused.

Baseline structural model decisions: **5 → 0** at equivalent source checkpoints, replaced by five engine-owned contracts. Host runtime choices removed: nine, unchanged from prior implementation. Three declaration-coverage exclusions are separate from removed model decisions. This harness iteration adds no planner decisions. The twenty answered model decisions and one queued identity decision are counted separately; no provider-call savings are claimed. Deterministic canonical identity assignments and governing attachments are not established because admission did not commit. Uninstrumented engine decisions remain unknown.

## 8. Next gate

**Architecture review.** Inspect the remaining effect/occurrence ambiguity and budget evidence before separately authorizing any future run. No additional execution is included. MIXED and Stage 1 remain **not run**.
