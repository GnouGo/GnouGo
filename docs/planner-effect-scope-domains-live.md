# Scoped effect-domain live diagnostic

Production was frozen at `3cde040d451b22b8ea5141fac3858f2371656c09` after the [full offline validation](planner-effect-scope-domains-offline.json). Campaign `schema5-effect-scoped-domains-diagnostics-1` started LOCAL once. MIXED was not run because LOCAL stopped; Stage 1 was not started.

## Result

The cross-workflow combination is no longer admissible, and did not recur. Both live effect assignments passed their scoped response schema and selected only `main` effects with `main` declarations. Interpretation completed with zero unresolved runtime scopes, no external/lifecycle/human action evidence, and preservation remained non-executable declaration evidence.

LOCAL then stopped with `INTENT_OPERATION_UNRESOLVED` at `/operations/@r_4f8bb7e7035e31492ebcd01e`: **Governing effect evidence requires established compatible realizations.**

The classifier contribution selected two same-scope alternatives (an invocation and the public-result realization). The normal bounded identity decision chose `operation_e226ff9ed819b5a645e7d9de`, the `main` result effect consuming `record` and `threshold` and producing `classifiedResult`. The descriptive-local contribution selected `governs` for `operation_74e8545e2a881a7f38f83094`, a separate invocation anchor without a realized operation. Governing validation stopped before atomic admission. Neither the canonical operation set nor its governing attachments was committed.

This is a verified semantic mapping/admission blocker, not a provider failure or recurrence of the scope-domain bug. One standalone identity decision was requested, so the diagnostic's zero-identity-decision acceptance requirement was also not met. Other staged producer references have not passed the later dependency gate and are not claimed valid.

## Usage

| Decision class | Verified calls | Distinct decisions | Input tokens | Output tokens | Reasoning included in output |
|---|---:|---:|---:|---:|---:|
| Interpretation | 10 | 22 | 23,333 | 4,351 | 2,796 |
| Effect grounding | 1 | 2 | 5,658 | 1,010 | 512 |
| Occurrence identity | 1 | 1 | 351 | 187 | 137 |
| Total | 12 | 25 | 29,342 | 5,548 | 3,445 |

All 12 durable reservations have receipts. There were no unverifiable dispatches, output partitions, singleton escalations or semantic repairs. Derived final-answer usage is 2,103 tokens. The largest request was 6,103 estimated / 5,658 actual input tokens, within the unchanged 9,600/12,000 input limits. All requests used `low` reasoning and the normal 8,192 output ceiling; the bounded 16,384 escalation remained configured but unused.

The supplied declaration fixture retained required `record`, optional `threshold` with omission default `100`, and required `classifiedResult` with its constraints. This diagnostic does not claim fresh declaration convergence or completed workflow execution.

## Evidence and integrity

The effect response and identity selection both passed their original structured schemas. Read-only replay reproduced the same stop from completed pages with zero provider calls, no new receipt replay and unchanged checkpoint/budget. The exact staged assignments, effect anchors and request/receipt fingerprints are in the [redacted report](planner-effect-scope-domains-live-report.json); raw requests and receipts remain encrypted.

All 22 production DLLs and the frozen harness hash remained unchanged during the diagnostic. Model, all-low profiles, scenario, catalog, policy, declaration fixture and budgets matched the previous manifest. Archived accounting remained unchanged. No production patch, provider retry, replacement diagnostic or Stage-1 run followed the failure.
