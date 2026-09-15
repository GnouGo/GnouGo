# Exact duplicate runtime evidence

Production was frozen at `1e01edcc6addc7d74a2baa6ca27a80991023ad56` after offline validation.
The change is limited to normalized runtime-evidence parsing. Exact duplicates keep
their first occurrence. An identity collision compares the entire typed record,
including requiredness, baseline authority, resource metadata, execution scope and
proof. Unequal records stop with located `INTENT_RUNTIME_EVIDENCE_CONFLICT`.

No model correction, repair charge, new phase, response-schema change, migration,
reasoning change or token-limit increase was introduced. Runtime proof 3, admission
proof 4 and Schema-5 storage remain unchanged. Source receipts are retained verbatim.

## Offline results

- All 3,401 solution tests passed across 29 projects; one existing optional live test
  was skipped. The planner suite passed 928 tests, including 13 new regression cases.
- The 13 new cases failed against the original parser. They cover exact contract,
  policy, directive and executable duplicates; requiredness/baseline/resource
  collisions; distinct action spans/kinds/roles; stable ordering and fingerprints;
  and completed-receipt replay with zero repair allowance.
- The solution and harness builds, both frontends, four packages, Native AOT planning
  and encrypted restart, and trimmed Agent.Server persistence passed without warnings
  under the existing documented publish exceptions. No new suppression was added.
- The published operation fixture collapses three copies of each action/governing
  assignment before encrypted persistence. Two restarts preserve its canonical
  operation, completed identity decision, accounting and zero repair usage.
- The retained output-declaration response was replayed unchanged: three `contract`
  entries now produce one evidence record. Full captured interpretation completes
  with 23 runtime-evidence records, zero new reservations, zero repairs and unchanged
  persisted history. This is offline evidence, not a fresh live result.
- The six classifier/batch and 18 CodeReview reference cases and both admission
  fixture selfchecks passed without model calls.

[Offline checks and retained-response replay](planner-runtime-evidence-deduplication-offline.json).

## One fresh LOCAL diagnostic

`schema5-runtime-evidence-deduplication-diagnostics-1` started exactly one LOCAL case.
The model, all-low profiles, frozen declaration ports and attachments, catalog,
policy and budgets match the previous diagnostic. Limits remain 12,000 input,
9,600 dispatch target, 8,192 normal output, bounded singleton 16,384 escalation,
and 16 durable reservations per case.

LOCAL stopped during interpretation when request eight failed without a durable
receipt. The retained exception says “The LLM provider is temporarily unavailable.”
The first blocker is provider availability; the retained exception text does not
establish an HTTP status. Usage for that request is unknown. The seven completed
responses pass their exact original schemas.

Canonical operation admission was not reached. No operation set, governing
attachments or canonical identity decisions were committed. The expected local
operation set and zero unresolved runtime scope are therefore unproven by this run.
MIXED is **not run** because its prerequisite did not pass.

| Measurement | LOCAL | MIXED |
|---|---:|---|
| Canonical operation IDs/kinds | Not established | Not run |
| Governing attachments | None committed | Not run |
| Deterministic / model identity decisions | 0 / 0; admission not reached | Not run |
| Verified responses / durable reservations | 7 / 8 | 0 |
| Unverifiable dispatches | 1 | 0 |
| Known input / output tokens | 12,301 / 1,665 | Not applicable |
| Known reasoning tokens, included in output | 760 | Not applicable |
| Partitions / singleton escalations | 0 / 0 | Not run |
| Semantic repairs | 0 | Not run |
| Largest estimated / verified actual input | 3,678 / 2,150 | Not applicable |

Read-only audit retained the missing receipt as unverifiable and made no provider
calls. It stops at the missing runtime-proof boundary; it cannot supply the absent
response or finish interpretation. All 23 frozen production/harness DLLs and
archived accounting were unchanged after the run. No post-run production patch,
provider retry, replacement diagnostic, MIXED start or Stage-1 campaign occurred.

[Redacted live report, request/receipt fingerprints and audit](planner-runtime-evidence-deduplication-report.json).
