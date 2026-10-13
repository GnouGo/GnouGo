# v15 live preparation stopped during explicit revision

The user authorized one fresh live test on candidate
`e0095f178403e417881c0bf7a4151d3a2a1a6817`, with at most ten products.
**Amazon execution did not start.** The initial proposal compiled but failed manual
business review. Its explicit revision stopped after three HTTP 500 responses and
one transport attempt with unknown completion. No revised response was committed,
and no artifact was approved or executed. PR #117 remains draft.

## Frozen candidate and prerequisites

The run is `exactdescriptions20261013a-amazon-1` in campaign
`schema-portability-20261002`, with the unchanged **EUR 200** ceiling.
[Authorization](authorization.json), [freeze](freeze.json), [709 binary hashes](binary-hashes.json),
[business prompt](prompt.txt), and [candidate CI](candidate-ci.json) identify the boundary.
The exact candidate had 29 successful applicable CI checks and four intentional
release-only skips. Its offline acceptance previously passed 5,491 solution tests,
13 existing skips, zero failures/warnings, local independent workbook/recovery
oracles, package/Native AOT checks and skill validation. This live introduced no
production change. The previously reported isolated sandbox timing stall remains
unproven as fixed; it did not recur in this planning attempt.

Fresh configured pricing/currency readiness passed with the existing ECB quote
from 2026-10-12, inside the unchanged freshness policy. The initial ledger was
EUR 143.595922, including EUR 11.902902 reservations; no unclosed logical request
blocked admission. The [local readiness probe](local-readiness.jsonl) independently
confirmed Browser acquisition, closure and a real XLSX readback, with zero model
calls and no Amazon access. That disposable probe is not live business acceptance.
The benchmark build passed with zero warnings/errors. No production binary changed
after freezing. The harness's legacy cohort manifest still carries its historical
matrix fields; only this one Amazon run was authorized. The run-level frozen oracle
is unchanged `observed-products-v2`, not an expanded cohort or code-review test.

## Planning and review

The initial two calls reached final review at **revision 2**, without automatic
repairs, in 57,315 ms. The artifact was
`7cd13391782f82a6c02eafbdb758605138cbd7ed886457d7b0aa82885f130c81`.
[TaskPlan](taskplan-r2.json), [YAML](workflow-r2.yaml), [requirements](requirements.json),
[review findings](review-r2.json) and [initial result](planning-r2-result.json) are retained.
Compilation was not treated as business acceptance. Review found:

- An unrequested consent interaction and supporting branches added by the model.
- Whole snapshots passed to extraction, with product selection mixed into mapping.
- Missing offered-ID/original-record reconnection and complete-acquisition assertions.
- A mandatory nonnull-URL assertion followed by an unconditional mandatory
  missing-URL assertion in the same product path. Both cannot succeed for one item.
- LLM calls for already typed product copying, worksheet rows and boolean status.

One [explicit structural revision](revision-r2-command.json), using the existing
`revise` mechanism, preserved the accepted requirements, public output contracts,
ten-product bound, actual search/visits, writer destination and unconditional cleanup.
It requested per-page extraction, compact ID decisions, strict offered/original
lookups, exact complete description blocks without requiring a heading, and typed
row/header/status assembly. It added no production rule, operator or repair authority.

While that request was pending, a [disposable typed proposal](disposable-taskplan.json)
was compiled offline against this session's original contracts. Its
[compilation report](disposable-compilation.json) and [YAML](disposable-workflow.yaml)
show zero diagnostics, 26 steps, five sets and seven workflows. This used zero model
or MCP calls and left the saved session unchanged. It is **not** a committed model
response, an approved replacement, or an executed workbook proof.

## Transport failure and durable stop

The revision request is:

`exactdescriptions20261013a-amazon-1:3:c7bdf5d19bfb4cf07ea29533b8849be83d36e596de5f6c8c4e16f0080358d333`

Its pinned automatic transport policy recorded **[500, 500, 500, unknown]**.
The final saved exception is `LLMClientException`, kind `Transport`, retry exhausted,
four attempts, without a final HTTP status or safe provider code. No more specific
provider cause is established. The final attempt is not counted as zero usage.
The request has no committed response; it was not replayed, closed or reclassified.

The planner durably stopped at **revision 4**, with no current artifact hash and
`MODEL_DISPATCH_UNVERIFIABLE`. [Attempt records](transport-attempts.json),
[failure metadata](failure.json), [diagnostics](diagnostics.json), [state](session-state.json)
and [run result](run-result.json) preserve the failure. There were three logical
requests, six physical attempts, zero automatic repairs, one explicit revision and
two discovery reads. Cumulative active planning time was **1,290,058 ms**.
No allowance was reset and no replacement session was created.

## Execution, independent oracles and accounting

- Amazon navigation / product visits / execution inference: **zero**.
- Execution workbook / artifact approval: **none**.
- Live visit, exact-description, workbook and cleanup oracles: **not run**.
- Known paid usage: **19,817 input / 4,633 output tokens**, **EUR 0.212928**.
- New uncertain-attempt reservation: **EUR 1.432515**, fully retained.
- Campaign known cost: **EUR 131.905949**.
- Campaign total retained reservations: **EUR 13.335416**.
- Campaign upper bound: **EUR 145.241366 / 200**, leaving approximately
  **EUR 54.758634** after reservations.

The [after ledger](campaign-after-planning.jsonl) confirms that request 3 is the only
new unclosed logical request. Historical closures, approvals, receipts and all older
reservations remain unchanged. This request now blocks new campaign calls through
the existing receipt gate. No archival or reconciliation action was authorized or
performed. Another paid attempt requires resolution or explicit permanent retention
of that request and fresh authorization; any eventual workflow also needs its own
revision/hash-bound artifact approval and requirement acknowledgments.

Only evidence is added to PR #117. The completed v14 run and its missing-description
verdict remain untouched. This attempt establishes neither a successful Amazon E2E
nor future model compliance with the new description guidance.
