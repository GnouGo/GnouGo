# Initial-composition live: revised artifact ready for review

The authorized structural continuation now reaches **revision 9 / final_review**,
artifact **`fc751e55508433f9e32a3851f64ef45b49a6502ea338a970dcf173b43821e4dd`**.
See the concrete [artifact review](amazon-review.md), [TaskPlan](taskplan-r9.json)
and [generated YAML](workflow-r9.yaml). Execution remains unapproved and not started.
The chronology below preserves the initial failure; the continuation is recorded
at the end. No production code or historical receipt was changed.

## Initial generation, before the authorized revision

The authorized fresh session **`initialcomposition20261013a-amazon-1`** stopped at
revision **3**, with no artifact, approval, Amazon execution or workbook. The updated
guidance and schema hints reached the model, but the proposal still contains invalid
bindings and unsuitable composition. This run does not establish improved initial
model compliance or business acceptance. PR #117 remains draft.

## Authorized archival and frozen candidate

The user explicitly authorized retaining request 3 of
`exactdescriptions20261013a-amazon-1` as inconclusive before continuing this fresh run.
The existing `retain-exhausted-request` operation appended an
[inconclusive closure](archival-closure.json). Its `[500, 500, 500, unknown]` history,
original session, requests, receipts and all reservations remain unchanged. Replay
of that exact request is permanently prohibited.

[Verification](archival-verification.json) confirmed all 13 protected records were
byte-identical, no other unclosed request blocked admission, and the campaign upper
bound remained **EUR 145.241366 / 200**. No historical invocation was replayed.

The fresh run uses candidate `bd83e46e61667ce846683cf01f34f3ea292c30ca`, unchanged
`compact-bindings-v15`, at most ten products and oracle `observed-products-v2`.
[Authorization](authorization.json), [freeze](freeze.json), [709 binary hashes](binary-hashes.json)
and the [business prompt](prompt.txt) identify the boundary. The isolated runner
stayed on that candidate; these evidence files do not change its build.

The candidate had 29 successful applicable CI checks and four conditional skips.
Its full solution passed 5,502 tests, with 13 existing skips and no failures;
Release/package, Native AOT, encrypted recovery, skill and local independent workbook
gates passed before this live. The fresh benchmark build had zero warnings/errors.
Pricing/currency readiness passed. The local Browser/Document probe independently
verified page acquisition, actual XLSX readback and Browser closure with zero model
calls. That probe is distinct from Amazon acceptance.

## Actual planning result

1. Discovery completed with one model call.
2. The [initial proposal](proposal-r2.json) supplied `requirements: null`. Validation
   returned `REQUIREMENTS_INVALID` before accepting a TaskPlan.
3. One existing automatic repair supplied [requirements](requirements.json) and the
   [revised proposal](proposal-r3.json). Contract validation then stopped with three
   `TASK_FIELD_UNKNOWN` findings and `REVISION_REQUIRED`:
   - `observationSnapshot.pages` was used as one literal property name instead of
     nested typed selections.
   - A mandatory `requires` referenced the invented field
     `availableReadonlyCopyStatusOkConsideringPotentialNamingMismatchButUseAvailableFieldDirectly`.
   - `rows.length` was used as an object property rather than an actual collection
     contract.

The immutable guard is outside automatic repair authority, so the planner correctly
stopped rather than requesting an ineffective consumer-only patch. The
[retained diagnostics](review-r3.json) preserve exact origins and messages.

Manual review additionally found interpretation used for producer-status checks and
typed row copying, whole observations supplied to interpretation/extraction, product
selection mixed into page extraction, and no strict offered-ID/original-record
reconnection. Fixing only the three malformed fields would not make this composition
ready for business approval.

The [actual request audit](request-presentation.json) confirms the new composition
guide, typed-copy guidance, complete-page example, `requires` hint and description
guidance were transmitted. Serialized durable request sizes were **20,551**,
**85,528** and **85,732 bytes**. These are request-envelope bytes, not provider token
counts. Guidance delivery is proven; successful model adherence is not.

## Transport and accounting

The fresh session used **3 logical calls, 1 repair, 2 discovery reads and 4 physical
attempts**, with **412,086 ms** active planning time and no explicit revision.
Request 3 used the existing bounded transport recovery: its attempts were
`[unknown, 200]`. The successful response has a committed receipt; the uncertain
attempt and its full reservation remain retained. No request identity was manually
redispatched and no allowance was reset.

- Known planning usage: **34,372 input / 8,642 output tokens**.
- Known planning cost: **EUR 0.385583**.
- New retained uncertain-attempt reservation: **EUR 1.308506**.
- Campaign upper bound: **EUR 146.935454 / 200**.
- Remaining after all reservations: approximately **EUR 53.064546**.
- Execution model/MCP calls, Amazon visits and execution workbook writes: **zero**.
- Live visit, exact-data, workbook and cleanup oracles: **not run**.

The full [r3 state/accounting](planning-r3.json) retains the exact decimal amounts
and request/receipt identities. Both rejected proposals and historical verdicts are
preserved. No production code, limits, compiler behavior or repair authority changed.

## Revision command prepared at the initial stop

An [explicit revision command](proposed-revision-r3-command.json) is prepared for
revision 3 with `preserveRequirements: true` and a null artifact hash. It confines
structural changes to data adaptation and guards: typed original-record projections,
complete per-page extraction, compact ID decisions, strict offered/original lookup,
verbatim description blocks, deterministic status/row assembly and actual producer
assertions. It preserves requested search, visits, accepted outputs, six-column
header order, destination, product limit and cleanup.

Task insertion/removal is outside the automatic repair's issued authority. The
prepared command was not dispatched at that initial stop. The user subsequently
authorized the scoped structural revision, with execution approval explicitly
excluded. That [authorization](revision-authorization.json) continues the same
cumulative allowance; it does not create another session.

## Authorized continuation and rejected intermediate results

1. The authorized r3 command produced [r5](proposal-r5.json). Although compilation
   passed, it removed individual visits, writing, cleanup and public outputs.
   [Business review rejected it](business-rejection-r5.json). Its small
   [generated workflow](rejected-workflow-r5.yaml) is retained as rejected evidence,
   not a simplification success. No execution was approved.
2. A complete [disposable proposal](offline-complete-proposal.json) reused the
   existing v14 typed composition with this session's accepted requirements,
   operation identities, literal arguments, destination and outputs. It compiled
   with [zero diagnostics](offline-complete-compilation.json) against the retained
   r3 contracts, with zero inference/MCP calls and no saved-session mutation.
   The r5 active catalog had omitted now-unused writer/cleanup contracts; the
   original catalog was read through encrypted-storage APIs for this check.
3. The [scoped follow-up](revision-r5-command.json) restored the full work in
   [r7](proposal-r7.json), without another discovery read. It incorrectly applied
   `field(searchUrl)` to an already scalar output and weakened two nonempty
   collection assertions. The [retained diagnostics](diagnostics-r7.json) stopped
   execution; no automatic repair could edit that immutable guard.
4. A four-path [explicit targeted revision](revision-r7-command.json) supplied the
   exact offline-compiled URL binding and guards. Its [committed patch](patch-r9.json)
   produced r9 with zero diagnostics. The normalized plan exactly matches the
   offline target; requirements, operation ordering/literals and cleanup remain
   unchanged. [Normal recompilation](compilation-r9.json) reproduces the saved
   YAML byte for byte and leaves the session unchanged.

The three new logical requests each completed in one HTTP 200 attempt. The same
allowance now records **6 logical calls, 7 physical attempts, 1 automatic repair,
3 explicit revisions, 2 discovery reads and 536,175 ms active planning time**.
No allowance, transport counter or financial reservation was reset.

- Known cumulative planning usage: **94,883 input / 17,269 output tokens**.
- Known cumulative planning cost: **EUR 0.887653**.
- Retained uncertain-attempt reservation: **EUR 1.308506**, unchanged.
- Campaign upper bound: **EUR 147.437524 / 200**; remaining **EUR 52.562476**.
- Execution model/MCP calls, Amazon visits and execution workbook writes: **zero**.
- Independent execution oracles: **not run**.

The exact [r9 accounting](planning-r9.json) and [issued-request audit](request-presentation-r9.json)
retain request identities, hashes, sizes and transport outcomes. All
[13 protected historical records](protected-after-revision.json) and
[709 frozen binaries](binary-verification-after-revision.json) remain unchanged.
The frozen candidate's full validation remains applicable: only planning evidence
was added. Initial model compliance remains unproved: reaching this review required
concrete offline composition and three explicit revisions. PR #117 remains draft.
