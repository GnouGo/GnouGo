# Initial-composition live: planning stopped before execution

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

## Concrete next action, not dispatched

An [explicit revision command](proposed-revision-r3-command.json) is prepared for
revision 3 with `preserveRequirements: true` and a null artifact hash. It confines
structural changes to data adaptation and guards: typed original-record projections,
complete per-page extraction, compact ID decisions, strict offered/original lookup,
verbatim description blocks, deterministic status/row assembly and actual producer
assertions. It preserves requested search, visits, accepted outputs, six-column
header order, destination, product limit and cleanup.

Task insertion/removal is outside the automatic repair's issued authority. The
prepared structural revision has **not been dispatched**. It requires explicit
authorization; it would continue the same cumulative allowance rather than create
another session. Any resulting artifact still needs its own revision/hash-bound
approval and requirement acknowledgments before one execution.
