# Frozen realization-ordering diagnostic rerun

The realization-ordering implementation remains frozen at
`2f8b177ec6076cfdedbf524bd72a01d3f1b457d7`. Only the isolated harness identity and
comparison identity changed for `schema5-realized-governing-diagnostics-rerun-1`.
The new LOCAL started with **zero reservations and zero consumed calls**, retaining
the sixteen-call ceiling. No archived accounting was reset or resumed.

Offline validation passed again: 3,455 tests across 29 projects, including 975
planner tests, plus 26 focused harness tests. One existing optional provider test
was skipped. The harness build was warning-free; the existing published Native
AOT planning/encrypted-restart and trimmed Agent.Server persistence smokes passed.
The six classifier/batch and 18 CodeReview reference cases passed without model
calls. Strict historical replay retained the original schemas and stopped at
missing evidence. The separate synthetic classifier replay still produces one
required local operation, `record + threshold -> classifiedResult`, with governing
evidence attached and zero standalone identity decisions.

## Fresh LOCAL result

Exactly one fresh LOCAL ran. It stopped during interpretation on an unverifiable
`LLMClientException`: **“The LLM provider is temporarily unavailable.”** The captured
exception does not establish a more specific HTTP or provider cause. This is a
provider/infrastructure reliability stop before effect grounding, not a verified
failure of realization ordering or a call-budget exhaustion.

| Measurement | Result |
|---|---:|
| Durable reservations / verified provider receipts | 5 / 4 |
| Initial interpretation requests | 3 |
| Partition pages created / dispatched | 2 / 1 |
| Singleton escalation requests | 1, unverifiable |
| Semantic repairs | 0 |
| Known input tokens | 6,640 |
| Known output tokens | 17,196 |
| Known reasoning tokens, included in output | 16,896 |
| Known final-answer tokens, derived | 300 |
| Largest estimated / verified actual input | 4,381 / 2,706 |
| Realization / governing / identity calls | 0 / 0 / 0 |
| Canonical operations and governing attachments | Not reached |
| MIXED / Stage 1 | Not run / not run |

The third request returned verified `output_limit` and was partitioned. Its first
singleton child also returned verified `output_limit`, permitting the existing
8,192 → 16,384 escalation. That escalation failed without a receipt for decision
`interpret_3ef1aed4ec4fb268eae6f1b9`, on page
`page_a4237432cefa49bea8c3acb705f25a45d1b9e03ddfc04c680cc75f89d4776834`.
Its token usage is **unknown** and is not included in the known totals above.

Read-only audit preserved all five request records, the four original receipts,
the failure, checkpoint and budget fingerprints. With interpretation incomplete,
offline admission replay stops at `INTENT_OPERATION_PROOF_MISSING`; it supplies
no synthetic replacement and makes zero provider calls. The declaration fixture
was frozen but its post-interpretation projection was not reached in this live run.
Zero live identity calls therefore does not establish admission convergence.

The model, all-low profiles, input/output limits, singleton escalation, scenario,
catalog, host policy and other budgets remained unchanged. All frozen production
and harness binary hashes and archived accounting were verified unchanged. No
production patch, retry, replacement diagnostic or MIXED run followed the stop.

The [redacted report](planner-realized-governing-rerun-report.json) contains the
manifest, exact request/receipt fingerprints, lineage, usage and offline audit.
