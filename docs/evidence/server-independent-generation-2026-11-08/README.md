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

## Live bounded retry and a masked timeout

The Server `local` check on frozen source `94a6078b` stopped before an accepted
contribution. Its [actual 6,142-byte request and two partial streams](local01-bounded-retry-and-whitespace.json)
show a short JSON prefix followed by prolonged whitespace, with strict provider
JSON Schema already enabled. Both captured 600-second deadlines expired. The
existing owner admitted exactly one separately reserved independent attempt, with
a fresh identity and no mixed response. No complete result or rescue is claimed.

The original planner diagnostic falsely reported session elapsed-budget exhaustion.
A cancelled progress write had remained in the HTTP context's storage-failure slot,
then masked the later terminal provider timeout. The same focused regression fails
against the earlier binary and passes after excluding supplied-token cancellation
from that slot. A genuine storage failure remains terminal before another send.
Additional native regressions preserve observed output-exhaustion/final-usage
barriers even if the protocol's last marker is lost. **377 AI.Core tests passed**;
**192 focused journal/retention tests passed**. The exact original failure records
are never rewritten; terminal owner evidence may establish eligibility for the
existing permanent-retention operation while keeping both complete holds.

A stronger generic 1.5-million-character alias fixture reproduces the nested memory
failure with the earlier evaluator and passes with the fix:
[before](synthetic-alias-before.json), [after](synthetic-alias-after.json).
The unchanged 50 MB ceiling rejects the earlier 54,005,388-byte materialization;
the fixed evaluator completes at 36,004,740 bytes. This is deterministic resource
evidence, not a live outcome or speed claim.

The [audit after the bounded live call](financial-after-local01.json) retains
EUR 97.282463054187192118226601 in HTTP holds and the separate original external
hold. Available funds in the same allocation are EUR 167.1193640841916703985669496.
The intermediate shared suite on `94a6078b` was stopped for the newly reproduced
generic defect; final shared validation remains required on stabilized source.

## Controlled visibility probe and compact field guidance

The [full-schema visibility comparison](local01-full-schema-visibility-comparison.json)
on `95aca234` keeps actual HTTP equal except the prompt. The real provider returned
complete, schema-valid numeric output declarations in **8.58 seconds**, one physical
call and EUR **0.0782445141065830721003134796** measured-usage tariff estimate.
The output includes the previously missing `required` field. This independent
probe applied no contribution and granted no approval/execution. A single successful
sample cannot establish causality or savings against unknown earlier usage.

Copying the complete recursive schema into normal intent prompts adds avoidable
source-paging pressure. The production change instead makes required declaration,
type-branch, default and clarification wire keys explicit in a compact field guide.
The complete response schema, strictness and local checks stay unchanged. Ordinary
complete-request measurement covers those new bytes before the identity is issued.
Exact issued recovery and rejection of an omitted public-output `required` field
are covered; **27 focused intent/paging/recovery tests passed**. A separate exact
changed-factor live probe remains required for the compact production guide.

The [local retry incident was permanently retained](local01-retention-applied.json)
through the standing authority, with both complete holds and every original record.
The old request/run cannot replay or continue.

A [read-only check through the actual configured Browser MCP](amazon-configured-aria-evidence.json)
returned a complete 1,585-record snapshot with no inference. Its bounded HTML
confirms ARIA list-item elements while current records group main product links
under `body`. This is additional source evidence, not a business-oracle pass or a
complete independently established occurrence/redirect proof. The delegated guard
and original oracle remain unchanged. Supporting a different qualification proof
requires a separately reviewed scope; no sponsored result or ambiguity is ignored.

## Code discovery startup and cancelled native evidence

The real `code` trial on `21a3d7c6` first encountered an unavailable code MCP.
The benchmark launcher had overwritten its inference-proxy setting with the
literal `discovery-only`; the actual server rejects that non-absolute URI during
startup. A real isolated process reproduces exit 134 with that single override
and advertises 46 original tools without it, without model dispatch or tool
execution. [The retained run boundary](code01-discovery-boundary.json) records
76 logical calls, 77 physical attempts and seven accepted checkpoints. No global
review, approval or execution occurred. The earlier snapshot at call 43 had no
accepted checkpoints; that intermediate count is not the final result.

The scoped harness correction preserves configured settings during metadata
discovery and supplies the real campaign proxy only during execution. It changes
no production proxy validation or operation permissions. A regression launches
the actual isolated MCP, verifies its contracts and checks configuration cloning.

No idle metadata-recovery boundary appeared during a bounded observation. The
owned diagnostic was stopped; its final cancellation followed native 2xx SSE
headers and incomplete bytes. Existing cancelled-request retention now recognizes
that exact native evidence, while rejecting complete results/usage, provider or
stream failures, mismatched modes/media, missing timestamps and malformed bytes.
Every original record and full uncertain hold remain, and the cancelled session
cannot replay or continue. Focused Server validation: **211 passed**, zero failures
or compiler warnings. The prior solution run was interrupted for this new fix;
it is not reported as a completed final-source pass.

The native partial regression includes the HTTP helper’s exact paired base64/text
capture; mismatched decoded text rejects retention. The [successful local and
collection runs](successful-server-cases-21a3d7c6.json) retain their exact source,
artifact approvals, unchanged oracle versions, measured cost and time through
accepted review and saved business results. They establish two scenario successes,
not Amazon qualification or controlled proof of general convergence gains.

## Selection dependency wire visibility

The next code trial on `d010bee6` stopped at logical call 3, before discovery or
accepted work. Its [exact generation and single-factor comparisons](code02-generation-and-visibility-comparison.json)
record two physical attempts: a 600-second whitespace timeout after opening the
empty-only dependency array, then a native transport ending about 301 seconds
after its last metadata bytes. The existing captured owner admitted precisely one
independent attempt and stopped at its allowance. No partial result was applied.

The existing protected probe requested `StructuredOutputStrict=false`, omitting
the HTTP `strict` member on the otherwise identical request. It completed, but
filled `dependencies` with prose despite its required zero length. Original host
validation rejected it; normal planning still sends `strict:true`. Another probe
kept strictness and the complete schema unchanged and presented that exact schema
in the prompt. It completed with schema-valid empty dependencies. Each probe had
one physical attempt, no candidate application and no execution. These isolated
observations motivate a compact field explanation; they do not prove causal or
general convergence gains.

New selection requests explicitly distinguish `{resultId, reason}` result
dependencies from prerequisites/global constraints and explain the existing empty
case. This changes neither the response grammar nor business authority. Preparation
measures and hashes the guide; historical issued requests recover exactly.
[Code run 01](code01-retention-applied.json) and
[code run 02](code02-retention-applied.json) are permanently retained with all
original bytes/holds and no replay or continuation. No funds were released.

Dependency presentation validation before the compact live probe: **160 focused
planning tests and 219 focused Server tests passed**, with zero failures or
compiler warnings. The prior `d010bee6` solution run was interrupted for this
newly reproduced generation defect; final shared validation remains required.

## Actual code metadata restored; discovery response presentation

The [compact dependency probe](compact-dependency-visibility-comparison.json)
changed only the HTTP prompt by 205 bytes. It retained `strict:true`, the complete
schema and source context, and returned a schema-valid response in one physical
attempt (24.09 seconds; 26.51 seconds through the saved probe report). No candidate
was applied. This is not an accepted contribution or causal convergence proof.

The next real code workflow on `0e10ea8e` passed the selection boundary and
[retrieved four real code capabilities without an unavailable warning](code03-discovery-visibility-comparison.json).
That verifies the launcher correction in the actual Server planning path. It
stopped at logical call 10 before accepted executable work: both native discovery
streams ended about 301 seconds after their last bytes, before their captured
600-second deadlines. The owner admitted one independent attempt, retained both
streams/holds and stopped at its allowance. The original request was
[permanently retained](code03-retention-applied.json), never replayed or continued.

An independent exact-request probe added only prompt presentation of the complete
response schema. It preserved `strict:true`, the schema and source context and
returned a schema-valid response in 10.87 seconds through its saved report. This
is evidence supporting response-contract visibility, not a successful workflow or
proof of an upstream root cause. The captured streams do not identify which
provider/gateway hop ended them.

The generic discovery fix presents the exact prepared response grammar only when
the complete serialized request fits its existing input target. Diagnostic mode
may use its captured phase maximum for this optional presentation. Otherwise it
retains the original prompt/context. Schema closures, strictness, evidence mapping,
request identities, finite admission and exact cold recovery remain checked. It
adds no inference call or executable authority. **276 focused planning tests
passed**, including context-preserving rollback, nested constraints and exact
recovery. The interrupted `0e10ea8e` solution run is not a final-source pass.

The focused Server probe/discovery/retention selection also passed **219 tests**,
with zero failures or compiler warnings.

## Business-block completion omitted from discovery

The real code run on `f87bbdbe` reached a favorable local review at call 30 and six
technically accepted checkpoints, then repeated metadata reads while trying to
finish its second business block. [Measured progress, cost and request components](code04-progress-and-cost.json)
keep that reviewed contribution distinct from global review and execution, which
were never reached. The optional schema presenter was verified inactive on actual
calls 5 and 10: the frozen 6000-token navigation input hint was too small for its
expanded request. This run therefore does not verify that presenter or establish
its causal effect.

The [read-only comparison on the exact retained state](code04-business-completion-comparison.json)
finds the concrete progression defect. Discovery did not offer `close` when the
current block had accepted work but neither an original requirement nor the root
scope could yet close. The lower-level candidate rules already permit a reviewed
business-completion claim without new tasks. Removing the single new local-close
branch from the corrected schema leaves every other schema field identical. The
TaskPlan hash, cancelled status, pending call and original snapshot stay unchanged;
this diagnosis dispatches nothing and applies no candidate or persistent change.

The generic fix exposes that existing claim only for the current, unreviewed owned
block with accepted, non-invalidated contributions and a current baseline. Its
explicit empty-scope selection prepares an identical-plan candidate without a
drafting echo, then runs ordinary validation and business review. Root scope,
original completion claims, global review, approval and execution remain separate.
The regression fails on the original source and passes after correction, including
wrong owner, unrelated/empty/invalidated work, stale state and repeated completion.
Broader validation: **633 focused planning tests and 222 focused Server tests
passed**, with zero failures or compiler warnings.

The owned diagnostic was stopped at call 100 after proving the missing choice.
Its last native request was cancelled after headers/partial bytes; all originals
and its whole reservation remain. The cancelled run cannot continue. At that point
the campaign had **EUR 3.3773757277205553067622013295** available after all holds. A fresh
standard 32k-output code trial cannot be admitted; even the last code request's
conservative input reservation is substantial. No funds were added or released.
A separately captured, lower-output native correctness check was the remaining
funded live hypothesis at that point. It cannot establish Amazon or code qualification. The
interrupted `f87bbdbe` solution run is not a final-source pass.

## Final frozen-source validation and live result

Production and harness source remained frozen at
`bcc0434f494590a97771a35a735aa28fcd216e86` throughout the final solution run.
[The completed validation record](validation-bcc0434f.json) reports **8,289 passed,
13 skipped, zero failures or compiler warnings, all 33 test assemblies**, exit 0.
The thirteen skips comprise seven native Windows PowerShell cases on macOS,
five opt-in live Copilot/GitHub cases and one opt-in isolated browser smoke.
The separate earlier-source browser smoke passed once; it is not relabeled as
final-source validation. The warning-free Release build and all seven final-source
package/publish/smoke checks passed, including Planning Native AOT execution and
the trimmed published Server's encrypted-storage smoke. No earlier interrupted
solution run is counted as complete. The following evidence-only commit changes
neither the tested source nor its attribution.

The final [real Server native workflow](native-final-success-bcc0434f.json) used
that same source and a newly captured `server-small-native.json` phase profile.
It completed in **seven logical calls and seven physical attempts**: local review
at call 5, favorable global review at call 7, exact delegated artifact approval,
real deterministic execution returning `result: 42`, and the unchanged independent
`native-result42-v1` oracle passing. No generated plan or model response was edited.
Known usage at captured tariffs totals **EUR 0.4410120913569189431258396775**.
Local review took 59.23 seconds; global review took 79.42 seconds. Publication of
the saved passing oracle took at most 239.85 seconds from first dispatch, including
the authorized artifact-review wait. The report's planning-finish timestamp is
not treated as execution completion.

This final profile retains the existing input limits and reasoning settings but
uses 4,096 output tokens for navigation/drafting and 2,048 for review. Truncation
still fails. It is a correctness check under a different captured profile, not
a controlled performance comparison or Amazon/code qualification. The earlier
native and Collections execution successes remain attributed to `21a3d7c6` and
their original profile, with both Collections variants passing their unchanged
independent oracle.

Amazon remains **0/2 consecutive qualified successes**. Its latest actual run
reached global review, approval and execution, but failed before the first product
read on the observed sponsored redirect route; the original independent oracle
also rejects unestablished product occurrences/redirect identities. The configured
Browser can access Amazon. A future qualification needs complete, independently
checkable card-occurrence and redirect/final-product identity evidence accepted by
the existing delegated routes and oracle. Sponsored results, URL identities,
permission guards and the verifier were not weakened or bypassed.

Code reached a favorable local business review and six accepted checkpoints on
`f87bbdbe`; it never reached global review, approval or execution. The missing
business-block completion choice is corrected and covered by generic regressions
on the final source. The small native success does not verify full code convergence.
Its [cancelled final request was permanently retained](code04-retention-applied.json)
with all original partials, accepted work and its whole uncertain reservation;
neither that request nor that cancelled run can replay or continue.

The [final campaign ledger](financial-final.json) contains **601 physical HTTP
attempts**, EUR **246.33783251231527093596059311** in known-usage tariff estimates,
EUR **148.30422749664128974473802064** in uncertain HTTP reservations across
22 unknown attempts, and the separate retained external reservation of EUR
**2.4215763546798029556650246305**. Within the unchanged EUR 400 allocation,
**EUR 2.9363636363636363636363616195 remains after every hold**. These are not
provider invoices; global funding and predecessor-liability reconciliation remain
unavailable. No funding was added, accounting reset or reservation released.
The last full code request alone reserved EUR 11.186278549037169726824899238,
which exceeds the remaining allowance. Another identical native trial supplies
no new hypothesis, and an identical Amazon trial cannot resolve its proven
evidence boundary. No further paid dispatch was made. Full code live verification
and the two-success Amazon target remain unmet. PR #120 remains Draft and unmerged.
