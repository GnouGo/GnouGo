# Read-termination diagnostics follow-up

Starting verified HEAD: `0719c65a8e8b7053faf3b00131d9019f7960f415`.
There were no intervening local changes at initial inspection. SDK05 and the
310-second local silence result remain reference evidence; neither was repeated.

The demonstrated local gap was diagnostic: Code20/21's broad `transport` failure
does not retain whether the native read reached EOF or threw. Their old records
cannot answer this retrospectively. The responsible remote layer remains unknown.

New attempts retain the received HTTP version, the first read-end observation,
UTC time, bounded framework error codes and cancellation source in the existing
HTTP journal. The existing inspector renders these optional facts; unavailable
historical values remain labelled unavailable. Underlying read errors are separate
from storage errors. A first-observation guard handles outer-reader cancellation
without a late continuation rewriting terminal evidence.

Acceptance and recovery remain unchanged. HTTP 200, EOF, useful content, native
completion and an attributable usage receipt remain distinct. No partial candidate
is applied, and unknown usage retains its reservation and replay barrier. No
timeout, connection pool, retry, model, provider, protocol or permission changed.

Controlled regressions cover EOF without terminal markers, peer reset, a captured
deadline, explicit caller cancellation, legacy byte-identical serialization,
source-generated restore, storage failure isolation, late-reader races and the
authenticated existing inspector. The new socket cases run with short fixture
delays; the prior 310-second test was not repeated. See
[recorded results](diagnostic-regressions.json).

The [short route-owner message](route-owner-message.md) is ready for review and
has not been sent. Its synthetic silent-versus-heartbeat route comparison requires
the owner's authorized access. No model probe was purchased merely to sample
another incomplete response. After an evidenced route correction or a distinct
testable hypothesis, use an independent identity/reservation for one representative
strict-schema request, apply no fragment, and verify native completion plus its
durable usage receipt before authorizing a fresh full Code qualification.

Full Code remains unqualified: Code21's four accepted checkpoints establish no
execution. All permanently retained sessions and financial history remain intact.
