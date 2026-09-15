# Frozen runtime diagnostic rerun

Exactly one fresh LOCAL case ran under
`schema5-runtime-evidence-deduplication-diagnostics-rerun-1`.
Production remained at `1e01edcc6addc7d74a2baa6ca27a80991023ad56`.
Only the harness campaign identity changed. The previous unavailable request and
its diagnostic were neither resumed nor retried.

LOCAL reached canonical admission and stopped with
`DIAGNOSTIC_ADMISSION_MISMATCH` at `intent_operations`, location `$`:
two local occurrences were established where the fixture requires one.
This is a verified planner/model convergence blocker. MIXED was **not run**.

The model assigned different requiredness to two descriptions of classification:

| Canonical operation | Kind | Action evidence | Required | Governing attachments |
|---|---|---|---|---:|
| `operation_82f2ecf1465dc8cb2cc923ba` | `local_processing` | `classifying a single record.` | false | 0 |
| `operation_d1c539152a0977221010c69e` | `local_processing` | Classification rules: rejected, high, standard otherwise | true | 3 |

The owned action records otherwise agree on the compatibility fields: local role,
generated-workflow scope, kind, absent baseline and absent resource semantics.
`PlanningOperations.Compatible` requires equal requiredness. Consequently, the
second candidate has no eligible existing root, and admission deterministically
creates another operation without an occurrence-identity decision. Read-only
revalidation accepts this two-root proof; the independent diagnostic assertion
then rejects its operation count. No production fix was made.

The approval-false condition, approval-true/threshold condition and standard
fallback all attach deterministically to the second root. The separate
deterministic/local-processing statement was retired by the one admission model
decision. There are zero external, lifecycle or human operations and zero
unresolved runtime evidence records.

The unchanged canonical declaration fixture supplies required `record`, optional
`threshold` with omission default `100`, and required `classifiedResult`.
The output retains its two enum/preservation modifiers. Preservation is contract
evidence and creates no operation. The coverage-exclusion count is zero because
no executable preservation candidate needed to be excluded in this run. This
diagnostic does not claim fresh declaration or full workflow convergence.

| Measurement | LOCAL | MIXED |
|---|---:|---|
| Verified calls / durable reservations | 13 / 13 | Not run |
| Unverifiable dispatches | 0 | Not run |
| Input / output tokens | 21,463 / 14,228 | Not run |
| Reasoning tokens, included in output | 12,464 | Not run |
| Largest estimated / actual input | 3,825 / 2,241 | Not run |
| Output partition children / singleton escalations | 2 / 0 | Not run |
| Semantic repairs | 0 | Not run |
| Deterministic root identity / model identity choices | 2 / 0 | Not run |
| Deterministic governing attachments / model attachment choices | 3 / 0 | Not run |
| Other admission decisions | 1 model retirement | Not run |

Interpretation used 12 calls (21,061 input / 13,679 output tokens); admission used
one (402 input / 549 output). Request eight returned a verified `output_limit` and
was handled by two successful partition children. There were 12 schema-valid
completed responses and one truncated receipt; all receipt usage is known.
Nine ConstraintsOnly runtime facets remained engine-owned. This is a count of
removed model decisions, not saved provider calls.

The configured model stayed `gpt-5.5-2026-04-24`, with all-low reasoning, 12,000
input, 9,600 dispatch target, 8,192 normal output, bounded 16,384 singleton
escalation and 16 reservations per case. The frozen scenario, canonical ports and
attachments, host policy, catalog, transport settings and global budgets match
the previous manifest. Session-specific references repacked the unstarted MIXED
preflight into 12 rather than 11 pages; its source decisions and inputs did not
change, and no MIXED request was dispatched.

Before dispatch, all 19 focused harness tests and both synthetic admission
selfchecks passed. The harness-only build passed with zero warnings and without
building project references. Existing full offline/package/publish evidence for
the unchanged production binaries remains in the preceding report.

After stopping, read-only inspection validated all completed responses against
their original schemas and revalidated the committed operation proof with zero
provider dispatches and no changed checkpoint. All 23 frozen manifest DLLs,
including 22 production DLLs, still match. Production sources are unchanged;
archived accounting and the previous diagnostic's before/after audit are identical.
No replacement case, retry, behavior acceptance, construction or Stage-1 run occurred.

[Redacted report, request/receipt fingerprints and read-only evidence](planner-runtime-evidence-deduplication-rerun-report.json).
