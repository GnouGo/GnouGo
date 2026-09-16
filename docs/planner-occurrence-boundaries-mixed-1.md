# Gate 2 — frozen occurrence-boundary MIXED

## 1. Outcome

**VERIFIED BLOCKER — category C. MIXED did not pass.** One fresh case, `schema5-occurrence-boundaries-mixed-diagnostics-1:mixed`, stopped before atomic operation admission on frozen production `cfc058fd52183854d09b2540b9f57d1e337797f9`.

Ten requests have verified receipts; six of sixteen calls remain. There was no provider failure, truncation, escalation, semantic repair or retry. Production was not changed. LOCAL was not rerun, and Stage 1 was not started.

## 2. First meaningful blocker

- Phase: `intent_operations`, after realization responses and before governing requests.
- Code: `INTENT_OPERATION_UNRESOLVED`.
- Location: `/operations/@runtime_082f2a85c350348c8f13eb21`.
- Finding: **“Governing evidence requires a compatible realized effect.”**
- Responsible realization decision: `effect_runtime_082f2a85c350348c8f13eb21`, in request 10, page `page_8952583bd67eccaa32a2ac5d77a83170f7a27254f9b291ab3f5c93b270235fd9`.

The classification action returned this original-schema-valid assignment:

```json
{
  "status": "governing",
  "evidence": [
    "r_0007ef5e2d74d4b49b8381b0",
    "r_153b8c8c38902c0f6bdd1683"
  ]
}
```

The references identify the complete classification-rules clause and its action span. Its issued domain contained one `main` result-realization candidate, `operation_e226ff9ed819b5a645e7d9de`, owned by canonical `classifiedResult`. The response did not realize that candidate.

Request fingerprint: `259dfd5c0c314b54169299f3e1ab6f0177d7283136635bcd0ee5e39cbff4bb1e`.
Receipt fingerprint: `6132b29b5e1a78c9cd5abccb37d272253aac40b61cfa16f7cbb3bf40db894f14`.
Full request identity and schema/context fingerprints are retained in the [redacted JSON report](planner-occurrence-boundaries-mixed-1.json).

## 3. Root cause

The realization page answered three contributions together. It realized the explicit external read as `operation_6b515f252d71b693829d2fe4`, consuming canonical `sourceId`; it deferred both the classification action and the input declaration's “one record to load” contribution to governing assessment.

Only the external read therefore had an established realization. That operation cannot serve as a compatible local-processing target. `DeterministicGoverning` correctly rejected the deferred classification contribution before issuing any governing request.

This is a realization-coverage failure: the response domain permitted the sole eligible local action to defer without another local realization. It is not a dependency cycle, requiredness mismatch, foreign-scope mapping or provider failure. Dependency resolution was never reached.

## 4. Authority analysis

The engine owned canonical declarations, occurrence domains, kind compatibility and the realized-effect set. The model owned whether an action contribution realized its issued effect or deferred to governing assessment. After the realization page completed, the engine knew there were zero compatible realized local effects and stopped correctly.

The complete candidate set also made the sole remaining local contribution observable before dispatch. What remains unproven is a general coverage rule connecting required executable behavior to realization obligations; one possible identity or a public output alone must not be promoted automatically into execution authority. The schema currently validates each contribution without requiring a compatible realization for every deferred contribution.

The issued external occurrence had independent `external_effect` boundary evidence. There was one possible local result realization and no speculative local invocation. Typed host projection supplied eight engine-owned clauses/nine obligations; baseline projection supplied five contract units. Both generated zero interpretation questions. Runtime interpretation had zero unresolved scopes.

The unchanged fixture supplied required `sourceId`, optional non-nullable `threshold` with omission default `100`, and required `classifiedResult`. The output declaration and preservation action spans were excluded through canonical declaration coverage: two excluded preliminary occurrences, not two saved provider calls.

Atomicity was preserved. The external realization was staged but no operations, dependency edges or admission proof were committed. No governing attachments were completed. Admission-only and post-commit restart acceptance therefore remain unproven for MIXED.

## 5. Proposed action

**Architecture review of required realization coverage versus governing contribution eligibility.** Keep the existing compatible-realization guard. Do not force all actions to realize, automatically merge effects, add a retry or change prompts/limits in this task.

The realization/governing authority boundary has recurred across retained failures. Review its complete-coverage obligation before considering another targeted domain filter. This is not evidence against the admission-owned dependency proof, which was not exercised.

No production patch or replacement diagnostic was made. Harness changes only authorize this MIXED case, compare effective typed settings to accepted LOCAL, strengthen occurrence/dependency acceptance and extend reporting. They introduce and remove zero planner decisions and change no proof version or public interface.

## 6. Validation performed

**Offline prerequisites:** 3,568 tests passed, zero failed, one optional live-provider test skipped across 29 assemblies. The focused harness/journal subset passed 133 tests. Harness and test builds were warning-free with production-reference rebuilding disabled. Published Native AOT planning/encrypted-restart, structural-baseline and trimmed Agent.Server persistence smokes passed. Six classifier/batch and eighteen additional reference cases passed without model or business transport calls. Synthetic fixture selfchecks passed separately, including detached restart and stale-proof rejection; these do not establish live MIXED convergence.

Existing production build, frontend and package evidence was retained and hash-verified. One USD→EUR prerequisite check returned a quote dated 2026-09-15. No quote was injected and no separate provider probe occurred.

**Live evidence:** exactly one fresh MIXED case used the configured `gpt-5.5-2026-04-24`, effective `low` reasoning, unchanged sixteen-call budget, 12,000 input ceiling, 9,600 target and 8,192/16,384 output handling. Interpretation finished in nine calls; one realization request followed. No historical runtime answers or receipts were imported.

**Read-only replay:** all ten retained responses passed their original schemas. Completed-page replay reproduced the same code and location with zero provider dispatches, zero receipt substitutions and unchanged checkpoint/budget bytes. The post-commit restart check was not reached; this failure replay is reported separately.

**Final integrity:** thirteen recorded production source hashes, twenty-one frozen tested DLL copies, both published executables, four packages and fixture hashes still match. All twenty-three running harness/production DLLs match the new manifest. Production source diff is empty; accepted LOCAL and all archived campaign/accounting fingerprints are unchanged.

## 7. Decision accounting

| Responsibility | Distinct decisions | Verified calls | Input | Output | Reasoning |
|---|---:|---:|---:|---:|---:|
| Interpretation | 9 | 9 | 27,005 | 2,636 | 1,573 |
| Realization/contribution grounding | 3 | 1 | 3,213 | 788 | 512 |
| Governing grounding | 0 | 0 | 0 | 0 | 0 |
| Boundary scope | 0 | 0 | 0 | 0 | 0 |
| Occurrence identity | 0 | 0 | 0 | 0 | 0 |
| Operation dependencies | 0 | 0 | 0 | 0 | 0 |
| Downstream relationships | 0 | 0 | 0 | 0 | 0 |
| **Total** | **12** | **10** | **30,218** | **3,424** | **2,085** |

Nine initial interpretation pages contained nine embedded runtime-facet decisions and produced eleven normalized model runtime items. Thirteen additional items were engine-owned policy/baseline evidence. Runtime-facet calls are included in interpretation, not additional calls.

One external canonical identity was resolved deterministically in temporary staging; zero canonical assignments were committed. The report's committed identity and attachment counters are therefore zero. Governing, dependency and relationship counts are zero because those decisions were not reached, not because MIXED convergence eliminated their work.

Coordinator reservations, durable journal requests and verified receipts all equal **10**. Unverifiable dispatches and unknown usage are **zero**. There were **zero partitions, singleton escalations and semantic repairs**, with **six calls remaining**. Largest estimated input: **5,730 tokens**; largest actual input: **4,196 tokens**.

No before/after provider-call savings are claimed. This harness-only iteration adds zero planner decisions and removes zero; accepted LOCAL and synthetic MIXED checkpoints are separate evidence tracks.

## 8. Next gate

**Architecture review.** Stop here. Do not rerun MIXED, rerun LOCAL, isolate a provider request or start Stage 1 automatically.
