# Server independent generation — 2026-11-08

The first fresh Amazon run on `79ea6ff0` stopped on its initial intent call,
without an accepted contribution, review, approval or execution. Its actual
6,925-byte HTTP request contained no tools. The native stream delivered 745 bytes,
then ended 299.56 seconds after the last bytes, before the captured 600-second
attempt deadline. [Transport and component measurements](amazon01-transport.json)
establish the boundary; they do not identify the responsible upstream hop or show
that request size caused the failure.

Exact receipt inspection found no completed response. The standing user authority
was applied through the existing [retention preview](amazon01-retention-preview.json)
and [immutable retention](amazon01-retention.json). The entire EUR
5.4795342588446036721898790863 reservation and all original partial evidence remain.
The old request/run cannot replay or continue.

The generic fix adds an explicit version-2 captured planning transport policy.
The existing AI.Core retry owner may create a fresh physical identity after an
observed tool-free transport loss. Every journal must admit and reserve that
attempt first. Response assembly starts afresh. The earlier attempt, partial bytes
and unknown usage hold remain; only the final complete response reaches planning.
Saved completion recovery retains precedence, and a loaded unresolved attempt
cannot independently dispatch after restart. Cancellation, permanent/provider or
protocol errors, received final usage and model output exhaustion do not use this
retry path. No TaskPlan, execution engine, model, endpoint or business oracle was
replaced.

The opt-in `server-independent-generation.json` profile permits three physical
attempts total, at most one independent attempt, existing backoff/jitter and
Retry-After, 600-second attempt deadlines and a hard 21-minute total generation
duration. Historical captures and the default confirmed-only policy are unchanged.
New Server captures bind the policy to encrypted tenant/session host authority.
The same shared campaign ceiling, funding identity, tariffs, known consumption,
external hold and every earlier reservation remain enforced.

Initial validation before the new live trial: AI.Core **366 passed**, focused Server
**241 passed**, transport-policy **16 passed**, zero failures/skips/compiler
warnings. These are deterministic regression results, not live qualification.

## First resumed live campaign and completion correction

Run `convergence-amazon-20261108-02` used frozen source `8ab8fd1d`, the same
medium-navigation/low-draft phase profile, native streaming deployment and original
oracle. Its new retry capture was verified in both journals. All 46 completed calls
used one physical attempt; no retry rescue occurred. Two coherent business blocks
received favorable local reviews, at logical calls 15 and 27. The run then repeated
source-evidence reads because final completion was unavailable. It was explicitly
stopped as a diagnostic at call 47; this was not cancellation of the user's overall
campaign. No global review, approval or execution occurred.

The [exact completion blocker](amazon02-completion-blocker.json) is a false
`OUTPUT_TYPE_MISMATCH` on a whole collection export. Graph validation described a
workflow call's declared exports as an open object while the compiler established
the same exports as closed. The runtime returns exactly those declared exports;
individual original member contracts retain their own openness/nullability.
[Changing only that validation fact](unchanged-plan-validation-comparison.json)
removes the diagnostic with **byte-identical compiled graph**, no accepted-plan or
saved-session changes, no inference, and no review/execution authority.

Selection's existing prefilter also stopped offering an original source after its
exact evidence had been loaded. Retained text and other still-needed source/page
links remain available, including after restore. The
[controlled response-choice comparison](source-filter-comparison.json) keeps the
prompt and session identical and reduces the response schema by 333 bytes. It is
not evidence of lower live time/cost or successful business execution.

A cancelled version-2 session uses the existing exact cancellation-retention path
with its own unchanged captured uncertainty allowance; it never inherits current
settings or grants a retry after cancellation. The new regression preserves the
whole hold and the cancelled run's replay/continuation barrier.

Measured through the first local review receipt: **15 logical/physical calls,
129.68 seconds, EUR 2.9943036274070756829377519033** in measured-usage tariff
estimates. Through the second: **27 calls, 244.99 seconds, EUR
6.1341334527541424093148231077**. The diagnostic ended with EUR
7.7900582176444245409762651140 known usage estimates and an additional whole
EUR 5.6678907299596954769368562471 cancelled-request hold. These estimates are
not provider invoices; no business-correct execution was reached.

Completion correction validation: **20 focused compiler/progress tests passed**,
including sequential and parallel real local execution preserving duplicate/null
records; the exact source-filter regression passed separately. The focused Server
journal/retention selection passed **202 tests**, including the new version-2
cancellation capture. The earlier full-solution run on `8ab8fd1d` was deliberately
interrupted after the next live defect required a production change; it is not
reported as a completed solution pass.

## Approved artifact reached real execution

Run `convergence-amazon-20261108-03`, frozen source `44c12c3c`, reached technical
validation, favorable global review, exact delegated artifact approval and real
execution. Its 47 planning calls cost EUR 13.93786833855799373040752352 in
measured-usage tariff estimates. One runtime interpretation cost EUR
16.11549484997760859829825347. These are separate physical calls, not invoices.
The complete search and cleanup succeeded. The first product read was rejected
before transport: its exact observed URL used `/sspa/click`, outside the delegated
read routes. No workbook was produced. The unchanged oracle failed; the planning
report's `passed` flag must not be read as execution qualification. See the
[execution boundary](amazon03-execution-boundary.json).

The read-only [original oracle check](amazon03-original-oracle-boundary.json)
also establishes unsupported redirect identities and ambiguous product occurrences
in the retained complete search. Those findings do not depend on generated rows.
The same denied URL appears exactly in the original observation. Neither the
permission routes nor the independent oracle were broadened. Another identical
paid trial would not resolve that demonstrated evidence boundary. Qualification
requires independently established result-occurrence and redirect/final-product
identity evidence; suppressing sponsored results, changing the query or relaxing
the verifier would not establish the original result.

Real failure reporting also exposed repeated JSON materialization in a closed
compiler expression. An offline comparison keeps its exact expression, data and
limits: [before](amazon03-mapping-before.json),
[after](amazon03-mapping-after.json). The original exceeds the nested 50 MB limit;
adopting fresh detached containers once completes at 37,421,560 materialized bytes.
Borrowed/reused values still copy independently; all original limits remain.
This is a targeted expression comparison, not an executed-workflow success or a
claim of lower live latency/cost. Flow component validation: **1,533 passed**.

SSE EOF within an unfinished event now has transport classification, allowing only
the existing captured independent-generation policy. Complete malformed frames,
cancellation and loaded uncertainty retain their barriers. Native framing and
three provider-path regressions pass; AI.Core component validation: **370 passed**.

The cancelled run-02 request was [immutably retained](amazon02-retention-applied.json)
with its entire hold. Run 03 has execution markers and is not eligible for
generation retention/replay. The [post-execution financial audit](financial-after-amazon03.json)
leaves EUR 178.0363546798029556650246298 available within the unchanged EUR 400
allocation, after all HTTP holds and the separate retained external hold. Global
funding remains unavailable. The solution run on `44c12c3c` was interrupted when
this newly reproduced runtime defect required a source change; no complete suite
pass is claimed for that intermediate state.
