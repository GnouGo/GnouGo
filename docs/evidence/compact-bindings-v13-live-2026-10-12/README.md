# v13 live preparation and exhausted-request archival

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
