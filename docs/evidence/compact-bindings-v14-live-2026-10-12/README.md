# v14 live: real workbook, one unsupported description absence

The one explicitly approved execution of `compactv1420261012a-amazon-1`
completed its ten product visits, actual XLSX writing and Browser cleanup.
**Independent acceptance failed:** product 3 has a blank description despite
five descriptive blocks already being available in its compact decision input.
The workbook and historical verdict are retained unchanged. PR #117 stays draft.

## Authorization and frozen candidate

The user authorized fresh planning, then explicitly acknowledged all six
requirements and approved **revision 7**, artifact
`62cc3e938a7fe7f1b24c7ea4ffb8606d721ccf744cc5c463144f5ef8679e1ba9`.
Approval advanced the saved session to revision 8 without changing that artifact.
[Execution authorization](execution-authorization.json), [approval command](execution-approval.json)
and the [pre-execution review](amazon-review.md) retain this boundary.

The candidate was `04213951a6020ecb484b1d0b190f06f112a03f82`, with
`compact-bindings-v14`, unchanged `observed-products-v2` oracles and at most ten
products. [Source/configuration/oracle freeze](freeze.json), [710 binary hashes](binary-hashes.json),
[prompt](prompt.txt) and [candidate CI](candidate-ci.json) are retained. The freeze's
`artifact_approved: false` is its original pre-planning state; it was not rewritten
after approval. No production code, contract, limit, permission or oracle changed
for this live. No historical invocation was replayed.

Configured pricing/currency readiness passed before execution. All old unresolved
logical requests were already retained inconclusive; their reservations remain.
The selected candidate had 29 passing applicable CI checks and four conditional
publishing skips. Its full solution passed 5,461 tests with 13 existing skips,
zero failures/warnings; 11 local Browser/Document oracle executions and applicable
package, Native AOT, encrypted-recovery and skill checks had also passed.
The local readiness probe independently exercised Browser cleanup and a real XLSX;
it is separate from Amazon execution.

## Planning, kept separate from execution

Revision 3 compiled, but manual review rejected full-snapshot extraction, learned
copying/row/status assembly, a missing workbook header and a literal output path.
Revision 5 replaced these with per-page extraction, compact ID selection, strict
offered/original lookup and the real writer output, but still used an implicit
interpreter for product assembly and omitted both truncation guards.

Two existing explicit structural revisions preserved accepted requirements,
public output contracts, external operations and cleanup. The final minimal
replacement was compiled against the retained original contracts in a disposable
offline harness before submission. It uses bounded typed scopes for nullable
fact assembly and ordered success/non-null/truncation guards. Revision 7 changed
only the requested replacements and independently recompiles to exactly the
retained YAML. No paid call or saved-session mutation occurred during that check.

Retained artifacts: [r3](taskplan-r3.json), [r5](taskplan-r5.json),
[r7](taskplan-r7.json), [approved YAML](workflow-r7.yaml),
[requirements](requirements.json), [first revision](revision-r3-command.json),
[final revision](revision-r5-command.json), [compilation measurements](compilation.json).

| Planning measurement | Result |
|---|---:|
| Logical calls / transport attempts | 5 / 8 |
| Automatic repairs / explicit revisions | 1 / 2 |
| Discovery reads | 2 |
| Active cumulative planning time | 1,215,385 ms |
| Known input / output tokens | 68,016 / 24,410 |
| Known cost | EUR 0.951619 |
| Unknown transport attempt retained | 1; EUR 1.378255 reserved |
| TaskPlan tasks / workflows / compiled steps / sets | 28 / 7 / 26 / 5 |
| YAML / JavaScript bytes | 217,493 / 19,843 |
| Technical-named declarations / singleton wrappers / `m.select` | 0 / 1 / 0 |

All eight planning transport attempts are consumed. No allowance was reset.
Remaining inference stages are query conversion, search extraction/selection and
per-product extraction/selection. Typed lookup, product copying and structured
row/header assembly require zero inference.

## Runtime and complete acquisition

Execution ran once for **741,320 ms (12 min 21 s)**. It performed 11 complete
acquisitions: the search plus ten individual product pages. All ten requested
navigation arguments came from original offered observations; all ten final URLs
were correlated through recorded producer evidence, including redirects.

- Search: **50 pages / 1,509 records**, yielding 137 compact candidates.
- Products: **154 pages / 6,005 records** across ten visits.
- Total: **204 pages / 7,514 records**, all processed and target-validated.
- Acquisition attempts: **14** across 11 captures. Search needed three attempts
  after two navigation invalidations; product 4 needed two. These were existing
  acquisition retries, not additional workflow executions. No empty-document
  reload recovery was reported.
- Final-generation HTTP status/method remain null where Browser could not verify
  them. The separate last HTTP response remains recorded as such; it was not
  promoted to evidence about another document generation.
- Workflow MCP calls: 11 navigations, one writer, one close. The unchanged oracle
  then made a no-active-page probe and a defensive close: **13 + 2 calls**.

[Acquisition metadata](acquisitions.json) preserves counts, generations,
invalidations, timings and HTTP status without duplicating observed page content
or tracking-bearing URLs. Full observations remain in encrypted campaign evidence.

### Mapping and resource measurements

All 11 mapping invocations succeeded: **13 generation attempts, two global
program repairs, zero shape specializations, zero cache hits**. Product 7's first
script lost the backslash in `\\S`; its next script still lost an escape in a
regex character class. Preflight rejected both before item execution; the existing
bounded global-repair path corrected them. They were not treated as data shapes.
[Retained scripts/receipts](mapping-receipts.json) and [telemetry](mapping-telemetry.json)
preserve those attempts.

28 complete pages were generation examples; the other 176 were still executed.
Every one of the 204 original pages validated. Search returned 137 candidates and
products 457 compact facts in total; these counts do not establish semantic
completeness or correct business selection.

| Runtime measurement | Result |
|---|---:|
| Logical LLM calls / transport attempts | 25 / 25 |
| Mapping calls / other interpretation calls | 13 / 12 |
| Known input / output tokens | 296,366 / 36,065 |
| Known execution cost | EUR 2.275073 |
| New uncertain execution attempts | 0 |
| Largest mapping generation request | 72,905 bytes |
| Largest retained prompt estimate | 45,998 tokens |
| Largest mapping allocation counter | 16,932,232 bytes |
| Largest materialization counter | 5,715,258 bytes |
| Maximum statements / active sandbox time | 1,796 / 41.9512 ms |
| Actual invocations / executed sets | 147 / 14 |
| Journal revision / retained events | 606 / 606 |

Prompt estimates above are the existing read-only inspection estimates, not a
claim that prompt text alone includes every provider wire field. Complete
request admission remained enforced by the existing 96,000-token path. The
50,000,000-byte, 10,000-statement and 5,000-ms per-invocation sandbox ceilings were
unchanged; all items and repairs shared their invocation's allowance. Aggregate
allocation across separate invocations is not peak memory. Process RSS, physical
block-write counts and checkpoint latency were not separately measured here.
See [request estimates](runtime-request-estimates.json) and [journal inspection](journal-inspection.json).
Journal inspection used public encrypted-storage reads only; no resume or execution
API was called during analysis.

## Actual workbook and independent failure

The successful writer returned the expected path:
`workflows/schema-portability-20261002/compactv1420261012a-amazon-1/products.xlsx`.
The actual file is **6,062 bytes**, SHA-256
`aa18ee34be437de6a71169f62682e3415e4d4e242d442b4845929d2143fa6fb0`.
It contains the exact supplied five-column header and ten product rows, with no
formula cells. Independent ZIP/XML inspection confirmed row/column positions and
that every cell matches the typed product output. No file was rewritten during
inspection.

The frozen oracle confirms ten visits, all ten names, all ten prices and nine
complete description blocks against the corresponding original observations.
All ten workbook URLs also match their exact navigation arguments. Browser's
workflow close succeeded; the oracle's following read failed with `No active page`.

**Product 3, worksheet cell B4, is blank.** Its committed interpretation response
is `descriptionId: null`. Its actual compact input contained 44 candidates,
including original references `record:136` through `record:140`, with complete
descriptive text blocks of 204, 118, 234, 174 and 101 characters. Their exact text
was already present in both the source observations and the offered decision view.
The [absence proof](description-absence-proof.json) records identities and hashes
without publishing duplicate observation text. This is a selection omission,
not a lost mapping result, rewritten description, lookup mismatch or XLSX loss.

The unchanged oracle findings are:

- `workbook_description_not_one_observed_block`
- `workbook_absence_not_independently_established`

[Oracle](execution-oracle.json), [per-product oracle evidence](product-evidence.json),
[independent cell checks](independent-cell-checks.json), [workbook verification](workbook-verification.json)
and [execution verification](execution-verification.json) retain the distinction:
**runtime success is true; independent acceptance is false.** Exact copying and
schema validity do not prove semantic relevance or justify an absence claim.

The minimal next investigation is generic selection guidance for complete observed
blocks: an intact descriptive feature block is eligible, without requiring a
separately labelled description section; null remains for genuine absence. The
retained offered candidates and null decision should become an offline regression.
Whether wording alone sufficiently improves future model behavior must be tested;
no deterministic absence proof is claimed. Do not weaken the oracle, fabricate a
fallback, make all descriptions nonnullable, rewrite this workbook or rerun the
completed artifact. No correction or additional live is included in this result.

## Accounting and delivery

Known planning plus execution cost is **EUR 3.226693**. Planning additionally
retained EUR 1.378255 for its uncertain transport attempt. The campaign upper bound
is now **EUR 143.595922 / 200**, including unchanged **EUR 11.902902 reservations**;
approximately EUR 56.404078 remains. No new uncertain execution attempt occurred,
and no unclosed logical request remains. Historical closures, receipts and
reservations were not reclassified or released.

[Before ledger](campaign-before.jsonl), [after ledger](campaign-after-execution.jsonl)
and [run result](run-result.json) preserve separate planning/execution accounting.
The run result's `status` retains its planning-stage value; saved approval and
execution fields, and the independent oracle, are reported separately above.

Only evidence is added to PR #117. Production source and the executed artifact
remain frozen. PR #117 stays draft because the complete business oracle did not
pass. Further paid planning or execution requires a new explicit authorization.
