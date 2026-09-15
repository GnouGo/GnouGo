# Realized-effect governing diagnostic

Production was frozen at `2f8b177ec6076cfdedbf524bd72a01d3f1b457d7` under
`schema5-realized-governing-diagnostics-1`. All offline prerequisites passed:
3,455 tests across 29 projects, 975 planner tests, 26 focused harness tests,
solution/harness and frontend builds, four packages, Native AOT planning/encrypted
restart, trimmed Agent.Server persistence, and reference-fixture selfchecks.
One existing optional provider test was skipped. The existing documented publish
exceptions were retained; there were no new warnings or suppressions.

Exactly one fresh LOCAL diagnostic ran. It stopped with `LLM_BUDGET_EXCEEDED`
before the first effect-realization dispatch. MIXED was **not run**; no Stage-1
session, replacement diagnostic, retry or production patch followed.

| Measurement | LOCAL |
|---|---:|
| Verified provider calls | 16, all interpretation |
| Initial interpretation requests | 12 |
| Output-partition child requests | 4 |
| Singleton escalations | 0 |
| Semantic repairs | 0 |
| Actual input tokens | 30,244 |
| Actual output tokens | 20,740 |
| Reasoning tokens, included in output | 19,055 |
| Final-answer tokens, derived by subtraction | 1,685 |
| Largest actual input | 2,848 |
| Largest estimated input, including the blocked request | 5,057 |
| Dispatched realization/governing/identity requests | 0 / 0 / 0 |
| Unresolved runtime scopes | 0 |
| Committed canonical operations | None; admission incomplete |

Two verified `output_limit` responses each split into two completed children.
Interpretation therefore consumed the full sixteen-call allowance. The next
coordinator request, containing four realization decisions, was recorded in the
journal before budget preflight rejected it. There are **17 coordinator/journal
request records, 16 provider receipts, and one budget-denied request**. The durable
budget records 16 calls and 50,984 total tokens.

The existing generic report labels the receipt-less entry as unverifiable. The
captured budget exception and journal ordering establish that this request stopped
before transport; it is not a provider-unavailable failure. The report is retained
unchanged, with this distinction added as audit evidence. Strict offline replay
inspects the original request schemas and receipts, then stops at
`LLM_BUDGET_UNVERIFIABLE` without dispatching or changing the checkpoint/budget.

The supplied declaration fixture still contains required `record`, optional
`threshold` with omission default `100`, and required `classifiedResult` with enum
and preservation attachments. Preservation has no standalone runtime candidate.
Interpretation also produced a preliminary external-write candidate for `Return`
in the output declaration. It never reached effect adjudication and granted no
execution authority. This observation is preserved separately from the terminal
budget blocker; no corrective answer was substituted.

Because effect grounding did not dispatch, this run provides no fresh proof of
canonical operation counts, dataflow, governing attachment or identity convergence.
The corrected synthetic replay remains the evidence for those behaviors. Zero
live identity requests here is a consequence of the early stop, not a convergence
improvement claim.

The configured model, all-low profiles, 12,000 input ceiling, 9,600 dispatch target,
8,192 normal output, bounded 16,384 escalation, fixtures, host policy, catalog and
budgets remained unchanged. All 22 production DLL hashes and archived-accounting
fingerprints were verified unchanged after the run.

The [redacted report](planner-realized-governing-live-report.json) retains the
manifest, page lineage, request/receipt fingerprints, raw accounting and post-run
audit. The first blocked realization page is
`page_a9d88976973feb18a088aa5917a6b8d05390e2045eeada70623a371ac12ee8e5`.
