# Browser acquisition diagnostics

Baseline: `9b80e8b5`, following the retained `genericbrowser20261009a-amazon-1`
failure. That approved execution remains failed and is not replayed. The user
reported that its page was visibly rendered. Rendering does not establish that
the requested lifecycle wait or complete snapshot capture finished.

## Findings and limits

The historical receipt records an empty first capture, one permitted reload,
a later HTTP 200 response and a shared acquisition timeout. It does **not** record
the phase that exhausted that deadline. Navigation generations include snapshot
replacement; their numerical difference is not a count of navigations. There is
no evidence establishing a consent or CAPTCHA blocker.

Two fresh, isolated Browser-only reads of the same search URL succeeded on the
unchanged baseline: 1,233 records / 38 pages in approximately 4 seconds and 1,551
records / 52 pages in approximately 4.5 seconds (approximately 9.9 seconds
including a separate local warm-up capture). The second used a visible browser.
The final document was `interactive`, with DOMContentLoaded timing recorded and
load timing still zero. These were read-only diagnostics, not paid planning,
mapping, product visits, workbook creation or an E2E acceptance run. No historical
invocation was resumed. A local 3,204-record acquisition also succeeded after an
empty HTTP 202 document and one reload, including a script-driven URL update.

The original timeout was **not reproduced**, so no speculative change to waiting,
reload eligibility, limits, permissions or acquisition acceptance was made.
In particular, a passing diagnostic read is not proof that the intermittent E2E
failure is resolved.

## Reproduced defect and correction

`DeadlineDuringInvalidatedCaptureRetainsNavigationEvidence` deterministically
navigates during capture and then exhausts the existing shared deadline. Before
the correction it fails: `Assert.Single() Failure: The collection was empty`.
The timeout bypassed expiration handling, losing a known navigation invalidation
from the returned receipt. Finalization of the capture now retains that cause
without retrying, publishing data or changing the terminal error.

The existing optional acquisition metadata now records its current phase, total
elapsed milliseconds and cumulative milliseconds per finite phase. Navigation,
readiness, selector resolution, capture, pagination, publication and reload can
be distinguished in error receipts. Timing does not reset between attempts.
Phase trace events contain no page payloads or URLs. Serialization omits absent
fields for historical records; current discovery exposes the additive metadata.
The exact returned response, including diagnostics, remains subject to the
original aggregate-size check.

Regression coverage includes capture/publication timeouts, cancellation, no
partial snapshots, exactly one navigation in the invalidation reproducer,
historical serialization, successful reload timing, and a main document whose
DOMContentLoaded is complete while a child is unfinished. Explicit `load` still
fails while that child is pending. Existing actual MCP transport and encrypted
recovery checks assert that the diagnostic fields survive restart without
inference, writing or repeated navigation.

Validation passed on production commit `d44047ad`: **141 Browser tests**, **88
affected host tests** (including local Browser/Document execution and independent
workbook assertions), and **5 actual transport/encrypted-recovery tests against
the Release self-contained osx-arm64 Browser binary**, all with `-warnaserror`.
The affected host selection took 25 minutes 40 seconds. Normal host builds also
completed their frontend builds. The complete solution suite was not rerun for
this Browser-only correction; Browser Native AOT remains unsupported.
Validation results, CI observations and reproducible commands are recorded in
`validation.json`.
There are no paid calls or campaign changes in this correction. The last retained
campaign upper bound remains EUR 120.79665899575455 / 150, including all prior
reservations. PR #117 remains draft. The historical E2E failure and the unproven
cause of its timeout remain explicit.
