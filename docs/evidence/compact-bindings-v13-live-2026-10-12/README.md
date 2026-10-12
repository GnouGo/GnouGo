# v13 live result and exhausted-request archival

The user authorized a fresh live test, then explicitly authorized the minimal
exhausted-request archival correction and permanent inconclusive retention of
request 3 from `typedcomposition20261011a-amazon-1`. This does not approve a future
workflow artifact or release any reservation.

## Deterministic correction

The retained request has four transport attempts `[unknown, 500, 500, 500]`, no
completion receipt and no verified usage. Its request-level retry policy is
exhausted, while its session used seven of eight attempts. The old request
archival check incorrectly required uncertainty on the final attempt.

The existing check now recognizes earlier uncertainty followed by recorded HTTP
failures. For a terminal HTTP failure, its status and attempt count must agree
with the adapter's explicit retry-exhaustion evidence. Every known response must
be an HTTP error; any successful response without a receipt still requires
reconciliation. No retry, accounting, runtime, compiler or limit changes occur.

The regression reproduced three rejected eligible orderings and a previously
accepted earlier-success case. After correction, all **77 campaign tests pass**
with `-warnaserror`, including unchanged evidence/reservations, restart,
idempotent closure, permanent no-replay, rejected successful responses and the
unchanged campaign ceiling. The correction affects only the benchmark archival
operator and its tests/documentation.

## Existing validation and live boundary

Production source remains the validated v13 source: full solution 5,420 passed,
zero failures, 13 existing skips; final additional large-observation regression
passed separately. All 29 applicable CI checks on `b9f70049` passed, with four
conditional publishing skips. Local Browser/Document readiness and configured
pricing/currency readiness passed with zero model calls.

Before archival the campaign upper bound is
EUR **137.52423908271879166629417691 / 200**, including every unknown reservation.
Archival, fresh planning and any later execution results will be reported
separately. Historical workflows and evidence are never replayed. A new artifact
requires its own revision/hash-bound approval and explicit requirement
acknowledgments. PR #117 remains draft.

## Archival verification

The existing operator command committed the authorized closure on candidate
`1ffe75d5`. Read-only rereads verify **13 protected record hashes unchanged**,
unchanged request/receipt counts and exact financial accounting. No unclosed
logical request remains. The closure permanently prohibits redispatch of the old
identity; it does not create a receipt, reclassify unknown usage or release money.

[Closure](archival-closure.json), [protected before](archival-before.jsonl),
[protected after](archival-after.json), [verification](archival-verification.json)
and [ledger](campaign-after-archival.jsonl) retain the proof.

## Fresh planning candidate

The authorized identity is `compactv1320261012a-amazon-1`, maximum ten products.
[Authorization](authorization.json), [freeze](freeze.json), [710 binary/dependency
hashes](binary-hashes.json) and [readiness](readiness.jsonl) are retained. The
production `src` tree exactly matches green `b9f70049`; only benchmark archival
eligibility, tests and documentation changed. New candidate CI runs separately.
At this preparation stage no workflow artifact had been approved. The approval and one completed execution are recorded below.

## Retained planning failures

Initial revision 2 stopped after two committed calls and zero repairs. Malformed
`field` operands also occurred in immutable `requires`, correctly producing
`REVISION_REQUIRED`. Manual review rejected full-snapshot interpretation, an
unrequested action and inference for typed row/status assembly. An explicit
structural revision was submitted within the same cumulative allowance.

Revision 4 corrected the field syntax and removed the unrequested action, but
misnamed three `each.output` fields. Deeper review also found the writer, cleanup
and accepted root outputs missing, and learned records incorrectly treated as
originals. This proposal was rejected for execution. The second explicit revision
requires restoration of the missing business work and deterministic original-ID
reconnection/row assembly. Accepted requirements remain unchanged. All retained
proposals, diagnostics and commands are preserved; no artifact approval was
issued and no Browser business execution started.

## Reviewed revision 8

Revision 6 compiled but manual review still rejected learned original-record
copies and insufficiently explicit selection bounds. A third limited explicit
revision supplied concrete existing typed page projections. Revision 8 reaches
`final_review` with zero diagnostics, unchanged requirements and restored business
operations, deterministic originals/rows, strict lookups and root cleanup.
See [artifact review](amazon-review.md) and [exact accounting](planning-r8.json).
At that review stage no workflow execution had started; concrete artifact approval was subsequently received.
The revised composition has 35 steps/8 sets; these counts are not an equivalence
comparison with the retained v13 compiler fixture.

All five fresh logical requests now have committed receipts: HTTP statuses were
`[200]`, `[200]`, `[200]`, `[500, 200]`, `[200]`. No new unknown completion remains.
The [post-planning archival reread](archival-post-planning.json) again matches every
protected historical hash and the exact closure. The user explicitly acknowledged all four requirements and approved revision 8 and its exact hash. No automatic requirement acknowledgment was created.

## Approved execution: failed, with verified cleanup

Candidate `1ffe75d5401ed2cdbe52fd0c2f7ff5ca9d8024c2` passed all **29 applicable CI
checks**, with four conditional publishing skips, before dispatch. Both the
[main build](https://github.com/GnouGo/GnouGo/actions/runs/38228405019) and
[planner validation](https://github.com/GnouGo/GnouGo/actions/runs/38228404429)
completed successfully. Their nested steps were checked, including the
non-blocking host job. [CI evidence](candidate-ci.json), [build](ci-build.json),
[planner](ci-validate.json). All 710 frozen binary/dependency hashes were
reverified; configured [pricing/currency readiness](pre-execution-readiness.jsonl)
passed without inference. The target workbook did not exist before execution.

The exact approved artifact
`243a67abc3c44ccdb97ef6fda8e1a8f794ab88501f3685fa47a38103af6a3d16`
was executed **once**. Approval moved the session from reviewed revision 8 to
approved revision 9 without changing its YAML. No historical workflow was resumed.

| Boundary | Observed result |
| --- | --- |
| Planning | Clean review after 5 logical calls and 3 explicit revisions; no automatic repairs. |
| Browser acquisition | Homepage: 3 pages / 134 records; search results: 54 pages / 1,540 records. Both complete, one attempt each, no invalidation. |
| Mapping | Every source page executed and validated. One generation per mapping, zero repairs/specializations. The search mapping nevertheless emitted 54 empty arrays. |
| Decision / assertion | `has_products: false`, `selectedIds: [""]`. The local `requires` failed before product visits and before the offered-ID lookup. The empty ID was never used in an action. |
| Business execution | **Zero product visits, zero writer calls, no XLSX.** Runtime failed in 80,718 ms. |
| Cleanup | Workflow Browser close succeeded. Independent current-page read confirmed no active page; the oracle's subsequent defensive close also succeeded. |
| Independent oracle | **Failed**: `workflow_execution_failed`, `workbook_missing`. |
| Durability | 13 normal steps, 1 finalization step; journal revision 64, finalization complete. All 14 invocation records are terminal, including the verified error; no unresolved invocation. |

The error is `INPUT_VALIDATION`, not resource exhaustion: the checked local
assertion received `false` where its contract requires `true`. Original source
location `task_259aaa6aa8d26466e249e4f2`, physical step
`n_057d824b69e5f524`, compiler role `requiredCondition` are retained in the
[error](execution-error.json). A terminal invocation is not a successful business
result. [Execution verification](execution-verification.json),
[event summary](execution-events-summary.json), [oracle](execution-oracle.json).

## Exact offline diagnosis

The first saved search mapping filters observed links using both:

```javascript
m.test(r.selector, 'h2')
m.test(r.text, '\S')
```

The selector string is an element locator, not authoritative DOM ancestry or
business classification. None of the 790 link-record selectors contains `h2`;
that predicate eliminates every link. Separately, the saved JavaScript string
contains a single backslash: JavaScript evaluates `'\S'` as `"S"`. The intended
non-whitespace regex would require preserving the backslash through the string
literal. These are semantic extraction mistakes, even though
the emitted empty arrays satisfy the result schema.

The disposable [public encrypted-storage replay](offline-replay/Program.cs)
loads the exact committed resolved input, original first script and approved item
contract. It evaluates every page in the existing sandbox, never the workflow
engine. It verifies unchanged input hashes and performs **zero provider calls,
zero MCP calls and zero storage writes**.

| Saved-script experiment | Pages | Emitted records |
| --- | ---: | ---: |
| Original script unchanged | 54 | **0** |
| Remove only selector-contains-`h2` predicate | 54 | 29 |
| Also correct only regex string escaping | 54 | 194 |

The two modified scripts are diagnostic ablations, **not approved replacement
business mappings**. Their records include different link purposes; neither count
proves a complete or correct product selection. The historical URL regex remains
in this reproduction only and is not a proposed production rule. The recorded
empty selection is preserved; no fabricated ID is accepted.

[Replay measurements](offline-mapping-replay.json) record 434.13 ms and 22,456,352
allocated bytes for the first, cold original evaluation. Subsequent ablations
use the warmed process and different outputs; their timings are not performance
comparisons. Build passed with `-warnaserror`, zero warnings/errors. The public
snapshot and all historical receipts remain untouched.

A minimal next correction should address generic extraction guidance and
regressions: select using actual observed properties rather than guessed locator
structure; test JavaScript regex escaping; preserve compact candidates for the
business decision. An empty view must not lead to invented placeholder IDs.
Neither contract validation nor source grounding proves semantic coverage of an
arbitrary filter. Keep the assertion, strict lookup, producer permissions and
independent oracle. No production fix, new architecture, limit change or live
retry is included in this evidence update.

## Runtime resources and accounting

[Mapping telemetry](mapping-telemetry.json) separates extraction from inference:

| Search-page mapping | Measured |
| --- | ---: |
| Generation examples / original pages processed | 3 / 54 |
| Verified complete mapping request | 53,242 bytes |
| Model generations / repairs / specializations / cache hits | 1 / 0 / 0 / 0 |
| Statements / unchanged allowance | 1,702 / 10,000 |
| Active sandbox time / unchanged allowance | 16.9633 / 5,000 ms |
| Allocated / materialized / output bytes | 10,444,808 / 5,979,540 / 108 |
| Unchanged sandbox memory ceiling | 50,000,000 bytes |

The 51 pages omitted from generation examples still executed. The mapping's
`failure_groups` diagnostic is retained verbatim; it does not mean a specialization
was dispatched. Neither mapping exhausted any measured allowance. Full source
observations, navigation URLs/titles and provider receipts remain encrypted;
public summaries omit their payloads. The current-page search read has no verified
HTTP status/method, so this report does not invent one.

[Exact accounting](execution-result.json):

| Phase | Logical calls / HTTP attempts | Input / output tokens | EUR cost |
| --- | --- | --- | ---: |
| Planning | 5 / 6 | 86,904 / 31,331 | 1.2196734404117490460555506256 |
| Execution | 4 / 4 | 44,097 / 1,931 | 0.2470627384861123435974798119 |
| Combined fresh run | 9 / 10 | 131,001 / 33,262 | 1.4667361788978613896530304375 |

Final campaign upper bound is
**EUR 138.99097526161665305594720733 / 200**, with approximately **EUR 61.009025**
remaining. The **EUR 10.524647007054257881912280394** unknown reservations remain
unchanged. All nine new logical requests have receipts, with zero new unknown
attempts. The four historical uncertain request identities are all permanently
retained as inconclusive; none is reopened or replayed.

[Final ledger](campaign-after-execution.json) and the
[post-execution archival reread](archival-post-execution.json) verify the same 13
protected historical hashes and identical closure. The inspection command also
prints a retained historical provider-schema diagnostic; it is not a new model
call or a failure from this execution.

PR #117 remains draft. Candidate CI passed; real execution acceptance did not.
This authorization has been consumed by the single execution. Any future live run
requires a fresh identity and the required new authorization/artifact review.
