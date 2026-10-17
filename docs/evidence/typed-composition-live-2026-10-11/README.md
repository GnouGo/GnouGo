# Authorized typed-composition live planning

Candidate `3d7c5d2d71a8722f4fb8ba5ebd2ab31de828cbea`, campaign `schema-portability-20261002`, cohort `typedcomposition20261011a`, run `typedcomposition20261011a-amazon-1`.

The user authorized one fresh paid planning session for at most ten products. Workflow execution requires separate revision/hash-bound approval and explicit requirement acknowledgments. No execution approval has been issued here.

## Freeze and readiness

The candidate passed the full offline gates and all 29 non-skipped CI checks (four conditional skips). The [CI snapshot](ci-before.json), [authorization](authorization.json), [business prompt](business-prompt.txt), [709-file binary manifest](binary-hashes.json) and [configured readiness](readiness-before.jsonl) are retained. The frozen checkout uses copied binaries whose hashes match the validated candidate. Production source, compilation profile, permissions, provider/model, token limits, oracle and EUR 150 ceiling remain unchanged.

The authorized provider/model is OpenAi / `gpt-5.5-2026-04-24`. Readiness made zero model calls and verified the existing pricing/currency policy. The candidate's earlier test results are not represented as new E2E execution evidence.

## Initial planning and review

Revision 2 reached `final_review` after two logical model calls, zero automatic repairs and two discovery reads. Static compilation reported no diagnostics. [TaskPlan](taskplan-r2.json), [YAML](amazon-r2.yaml), [requirements](requirements-r2.json), [responses](responses-r2.json), [accounting](planning-r2.json), [request receipts](requests-r2.json) and [measurements](metrics-r2.json) are preserved.

[Manual review blocked execution](review-r2.md): an unrequested unconditional click, whole-snapshot extraction, selection mixed into extraction, missing deterministic ID reconnection, inferred typed row/status assembly and a literal output path. The 11-task, 21-step, six-set artifact is smaller but fails the required composition; no correctness or performance improvement is claimed.

One [explicit structural revision](revision-r2-command.json) was submitted through the existing revision command within the same cumulative session allowance. It is limited to the reviewed data-adaptation defects and unrequested work. It preserves all six accepted requirements, public contracts, requested navigation, visits, real writing and cleanup. It does not change repair authority, edit approved YAML, grant execution permission or populate acknowledgments.

## Execution boundary

No Browser business navigation, product visits, mapping execution or Document writing is part of this planning-only authorization. Execution, workbook inspection and visit/cleanup acceptance remain not started. Historical sessions, reservations and verdicts remain retained. PR #117 remains draft.

## Final revision outcome

The session durably stopped at **revision 4**, with no current artifact hash, after **26m29.951s** cumulative planning time. The diagnostic is `MODEL_DISPATCH_UNVERIFIABLE`: no retained completion for the revision request, `ServiceUnavailable`, terminal **HTTP 500**. Its pinned retry policy exhausted four physical attempts: one transport failure followed by three HTTP 500 responses. There was no manual replay. [Failure/accounting](planning-final.json), [transport summaries](requests-final.json) and [exact decimal accounting](final-accounting-exact.json) are retained.

| Planning evidence | Result |
| --- | --- |
| Logical requests | 3; first two have committed receipts, third unresolved |
| Physical attempts | 7 |
| Explicit revisions / automatic repairs | 1 / 0 |
| Known input / output tokens | 19,878 / 3,433 |
| Known planning cost | EUR 0.1795900257343153784719141006 |
| Retained uncertain reservations from this session | EUR 2.6667273049960067441654095307; two attempts |
| Campaign known cost + retained reservations | EUR 126.99959207566453378438189652 + EUR 10.524647007054257881912280394 |
| Campaign upper bound / ceiling | EUR 137.52423908271879166629417691 / EUR 150 |
| Remaining financial headroom | Approximately EUR 12.475761; not permission to replay |
| Execution, runtime mapping, XLSX and E2E oracles | Not started |

Issued logical request envelopes are 20,633, 89,332 and 111,809 UTF-8 bytes. These are persisted request-envelope sizes, not a claim about the exact provider HTTP-body size. The first two issued request/schema hashes and receipts remain unchanged after revision; accepted requirements compare equal. Reservation ceilings are accounting estimates, not increased runtime token allowances.

The current blocker is external dispatch uncertainty, in addition to the rejected initial composition. No revised proposal was returned, so no claim is made that the requested composition corrections succeeded. The old proposal remains review-blocked. The unresolved third request remains open with all evidence and reservations retained; no closure, reconciliation, redispatch, new session or workflow approval was performed.

Only evidence files changed on the PR. Candidate source and frozen binaries remain unchanged. Future work must first resolve the stopped request through an authorized existing process; new spending and workflow execution retain their respective authorization and concrete artifact-review gates. No E2E success is claimed.
