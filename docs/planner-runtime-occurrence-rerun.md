# Frozen canonical runtime admission rerun

Production remained at `3b5b2167fc60c0d0a0fd7555167e8a7684163bf4`.
`schema5-runtime-occurrence-diagnostics-rerun-1` started exactly one fresh LOCAL
diagnostic. MIXED was not run because LOCAL stopped before canonical admission.
No archived request was resumed, no failed dispatch was retried, and no Stage-1
campaign or production patch occurred.

The model, all-low reasoning profiles, canonical declaration ports and attachments,
host policy, catalog, and budgets match the previous manifest. Limits remain
12,000 input, 9,600 dispatch target, 8,192 normal output, bounded 16,384 singleton
escalation, and 16 durable requests per case. Only the harness identity, explicit
LOCAL/MIXED start gates, and reporting changed.

## First blocker

`INTENT_OPERATION_UNRESOLVED` at
`/operations/@r_9595368567a8bbc18a027365`:
“Runtime evidence contains a repeated assignment.”

Decision `interpret_c181cf54a85cdff8848285fa` interpreted the clause:

> Return classifiedResult:{id:string,amount:number,category:string}, all members required.

Its completed response contains:

```json
"runtime": [
  { "role": "contract" },
  { "role": "contract" },
  { "role": "contract" }
]
```

The original response schema accepts this array. Deterministic parsing seals the
three entries to the same runtime-evidence identity and rejects the repetition.
This is a model-response/domain convergence blocker, with a verifiable receipt;
it is not a provider availability or output-exhaustion failure. The exact parsing
failure was reproduced offline from the retained response with zero provider calls
and unchanged archive state. No corrected or synthetic answer was substituted.

All ten interpretation pages completed before combined interpretation validation
encountered this result from request nine. `intent_operations` never started.
Runtime evidence and canonical operations were not committed; empty persisted
collections do not demonstrate the expected operation set or zero unresolved scope.
The declaration fixture remains an unchanged diagnostic precondition.

| Measurement | LOCAL | MIXED |
|---|---:|---|
| Result | Stopped before admission | Not run |
| Canonical operation IDs/kinds | Not established | Not run |
| Governing attachments | None committed | Not run |
| Deterministic / model identity decisions | 0 / 0; admission not reached | Not run |
| Verified provider calls / reservations | 10 / 10 | 0 |
| Unverifiable dispatches | 0 | 0 |
| Input tokens | 18,405 | Not applicable |
| Output tokens, including reasoning | 5,228 | Not applicable |
| Reasoning tokens | 3,523 | Not applicable |
| Interpretation pages / answered source decisions | 10 / 22 | Not run |
| Output partitions / singleton escalations | 0 / 0 | Not run |
| Semantic repairs | 0 | Not run |
| Largest estimated / actual input | 3,808 / 2,355 | Not applicable |

## Verification

All 19 focused harness tests and both synthetic declaration/admission fixture
selfchecks passed before dispatch. The harness build was warning-free and did not
rebuild project references. All 22 production DLLs and the tested harness DLL
matched their frozen hashes after the run; production sources are unchanged.
The harness verified archived accounting, and the previous diagnostic's read-only
audit was identical before and after this run.

MIXED preflight packed 10 pages instead of the historical 11. Offline changing
only the diagnostic identity reproduced this difference with unchanged source
inputs and 22 decisions; LOCAL remained at 10 pages. No settings or fixture changes
were made to obtain that result.

[Redacted report, exact assignment, fingerprints and replay evidence](planner-runtime-occurrence-rerun-report.json).
