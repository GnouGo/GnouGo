# PR 120 planning simplification

## Cumulative EUR 2,100 campaign, 20 November 2026

At 06:13:10 UTC the operator applied the EUR 400 supplement to the existing
campaign `pr120-live-20261107-400`. Read-only audits before and after the grant
agreed on all 2,555 previous HTTP attempts, EUR 1,119.75822 in measured usage
at captured tariffs, EUR 478.50045 in outstanding HTTP reservations and
EUR 99.88219 in separate application reservations. The new available funding
was EUR 401.85913. The authorization fingerprint is
`25041a40a3c9336a98364ba6cf232c01e0c1df3ee9cbe26fbe7685b1685ef834`.

### Amazon source and execution evidence

| Fresh source capture | Search pages / records | Source GETs | Ordered products identified | Business visits | Browser closed and verified |
| --- | ---: | ---: | ---: | ---: | --- |
| 09 | 74 / 1,558 | 21 | 10 | 0 | Yes |
| 10 | 74 / 1,582 | 21 | 10 | 0 | Yes |
| 11 | 0 / 0 | 1 | 0 | 0 | Yes |

Captures 09 and 10 each resolved 20 occurrences under the active grant.
Three reads returned product evidence and 17 returned store evidence. Cold
verification reproduced each capture's ordered selection. Capture 11 received
HTTP 202 on its GET, Chromium reported `net::ERR_ABORTED`, and the committed
document remained `about:blank`. It yielded no search records. All three used
zero model calls. The fresh policy description on `351b26ef` includes both
observed-navigation patterns and the separate static allowlist from the
Browser configuration. The source and publication checks continue to use
the existing permission grants. [Browser configuration regression](../../tests/GnOuGo.Agent.Server.Tests/AmazonAaxWorkflowTests.cs)

Amazon session `5be178ac35e84f66811070c4f7989f1e` stopped in planning
after six technical checkpoints. It produced zero business visits and no
workbook. Receipts 20 and 21 used an accepted control export inside a
predicate. Receipt 25 iterated the original collection used by a closed
projection. The original validator rejected that receipt at
`/tasks/indexed_source_pages`. Replaying the identical response through
the issued grammar and compiler on `6fea9bf1` accepted it, with the original
baseline `46c4da94bf9fbe19ab1914004b5272112ac09ffdef9c44848434be950917ed72`
and zero provider calls. [Control composition regressions](../../tests/GnOuGo.Flow.Planning.Tests/CoherentBusinessFragmentTests.cs)

The run added 27 HTTP attempts with measured usage estimated at EUR 11.46215.
A 28th application reservation of EUR 3.47928 had no campaign request,
HTTP attempt or receipt. Its full amount remains held. The subsequent audit
found 2,582 historical HTTP attempts, EUR 1,131.22037 in measured usage,
EUR 478.50045 in outstanding HTTP reservations, EUR 103.36148 in application
reservations and EUR 386.91770 available before the next live review.

### Review admission

The new Server guard checks mandatory terminal receipts before publication.
A failed check blocks approval, as do missing or conflicting completions.
An explained failure can support a request for changes. The review regression
uses the same verdict check as the live prepublication hook. [Publication regression](../../tests/GnOuGo.Agent.Server.Tests/CodeReportOracleTests.cs)

At head `107c86fc`, PR 718's root test script runs the recursive workspace
suite. The webapp's test script runs that package's Vitest suite. A green
member-only receipt previously satisfied the root check. The guard on
`a4c451e8` resolves package ownership from the pinned tree and recognizes
explicit workspace-root commands. [Root scripts](https://github.com/AxaFrance/SmartGuide/blob/107c86fc87fcd89b529f6589736d27a09acb4206/package.json) ·
[Webapp scripts](https://github.com/AxaFrance/SmartGuide/blob/107c86fc87fcd89b529f6589736d27a09acb4206/production/webapp/package.json)

Review session `bc53bd8d6b844466858c34847f9b03ba` on `6fea9bf1` accepted
eight technical checkpoints. Its TaskPlan contained URL and revision resolution,
one clone, a Copilot review and root cleanup. Execution never started. The
checkout directory was absent, and GitHub showed zero reviews and zero inline
comments after the session. [Target PR 718](https://github.com/AxaFrance/SmartGuide/pull/718)

The generator abandoned an unused fetch selection after constructing the clone
and comparison. Request 38 ended with provider finish reason `length` at 4,096
output tokens. The response contained 283 characters through the `outputNames`
key followed by 14,007 whitespace characters. It contained no completed
selection. On the same run, offline replay of request 32 reduced the canonical
prompt from 136,493 to 125,623 UTF-8 bytes by presenting an identical eight-task
array once. The replay compared every item in order and made zero provider calls.
[Task page regression](../../tests/GnOuGo.Flow.Planning.Tests/PlanningRepeatedTaskPresentationTests.cs)

The 38 HTTP attempts had measured usage estimated at EUR 27.25104. The
subsequent campaign audit recorded 2,620 HTTP attempts, EUR 1,158.47140 in
measured usage, EUR 478.50045 in HTTP reservations and EUR 103.36148 in other
reservations. EUR 359.66667 remained available for the next run.

In the next fresh review, session `4df6899ed88b44e191c17f1934ae01c7`,
request 5 displayed all five provider actions and their complete descriptions.
Its response requested three of those descriptions again. Requests 6 and 7
subsequently read directory pages. Fresh discovery grammars omit a second
description read when the current complete menu shows the exact original
for an inspected source. Partial menus, truncated descriptions and explicit
prerequisites retain the read. If reading the source enables reselection
for a new objective, the issued grammar also retains it. [Discovery grammar regression](../../tests/GnOuGo.Flow.Planning.Tests/CompactDiscoveryProtocolTests.cs)

Request 14 presented the same union constraints in a selected operation and
in the evidence for five output branches. Replaying the presentation on all
five expanded views reduced the prompt from 92,874 to 82,890 UTF-8 bytes.
Every branch schema and complete ancestor array matched its original bytes
after pointer resolution. Both replays used zero provider calls.
[Schema presentation regressions](../../tests/GnOuGo.Flow.Planning.Tests/PlanningPromptPresentationTests.cs)

Requests 17, 19, 21 and 23 asked for accepted task declarations immediately
after technical checkpoints. For example, request 19 displayed the clone
declaration and requested the previously accepted checks, new diff read and
cleanup. The ordinary 1 KiB preview had omitted all three declarations.
On an advisory run, an oversized continued scope can use its existing 8 KiB
preview allowance for up to three complete operation declarations. The most
recent checkpoint and its scope's cleanup have priority. The complete scope
remains retrievable. [Continuation regression](../../tests/GnOuGo.Flow.Planning.Tests/CompactAcceptedTaskNavigationTests.cs)

Request 47 named `classifications` as its independent extraction output
while declaring `matchesTargetedCandidate` as its only result field. The
compiler rejected the mismatch. Request 48 repaired it and the Server
accepted checkpoint 16. Fresh draft grammar asks for the field name once.
Offline replay removed just the redundant output name from the original
response. It passed the new grammar, technical validation and compilation
on the same baseline `33bbfe12a1b0538678ebbe21ff161e1f21ffe000a3e8e98b0f7b14999ad90e87`
with zero provider calls. The original response still fails its historical
compiler check. [Draft and compilation regression](../../tests/GnOuGo.Flow.Planning.Tests/CompactDraftFixedFactsTests.cs)

With 20 accepted tasks, request 44 linked pages at offsets 0 and 16.
Request 45 used a separate response to reach offset 8 through the first
page. The continued-scope directory now identifies intermediate pages by
their first and last local task. A cold regression retrieves offsets 0, 8
and 16 together using the ordinary evidence grammar.
[Task directory regression](../../tests/GnOuGo.Flow.Planning.Tests/CompactDraftNavigationTests.cs)

Request 66 assembled both the execution evidence and the proposed review in
accepted root tasks. It deferred block completion to export those results.
The original requirements explicitly declared zero public outputs. Request 67
retrieved evidence again, request 68 submitted the unchanged block, and its
ordinary business judge accepted it on request 69. In this situation, the
draft contract now says a later block can consume accepted root results
directly and the current contribution can claim completion when its result and
cleanup are covered. An unknown or populated public interface receives no such
hint. [Block closure regression](../../tests/GnOuGo.Flow.Planning.Tests/BusinessBlockCompletionAffordanceTests.cs)

The next block selected both accepted results. In discovery request 73, the
proposal object included the execution report as an exact member and the
report's schema appeared again as a standalone import. Replaying the request
with its original local schema references expanded produced 44,217 UTF-8
bytes on `93afd696`. Referring to the same field reduced that to 35,612 bytes.
The replay compared the complete expanded schema and both source bindings
with zero provider calls. [Imported result regression](../../tests/GnOuGo.Flow.Planning.Tests/PlanningPromptPresentationTests.cs)

On request 78 the generator bound the official GitHub review and inline
comment operations. Request 79 abandoned the selection because neither HTTP
tool declared a separate host-confirmation operation. Request 82 restarted
discovery for that operation. The qualification host already intercepts
`submit_pending`, checks the receipts, presents the concrete submission for
confirmation and rechecks the target before dispatch. Its discovery adapter
now describes this installed guard on the existing operation. The provider's
wire schemas, authorized methods and actual dispatch checks are preserved.
[Discovery regression](../../tests/GnOuGo.Agent.Server.Tests/DelegatedPlanningDiscoveryTests.cs)

### Amazon session 60e34eba2dfa44bdb98b7255a6d1e3ca

The fresh Server run on `e034bdde` lasted from 07:27:50 to 08:21:03 UTC.
It accepted 14 technical checkpoints, including a search and root cleanup.
It stopped after receipt 54. Execution never started. The run directory
contained no workbook and the workflow visited zero products.

Request 52 iterated `index_sponsored_literal_links.linkMarkers`, an output of
an accepted sibling control. The issued grammar accepted the response and the
compiler returned zero findings. Intermediate validation rejected it with
`CONSTRUCTION_STEP_INVALID`. A later repair proposed a lookup that failed
`TASK_LOOKUP_INVALID`. On `784b0ed4`, offline replay accepted response 52
with zero model calls and the same accepted-plan hash
`ccf6ffb1ff3fa0d425c324a682cc983d0b786b4470c72108b6cda6a758fb5fc3`.
The SHA-256 of the retained response JSON is
`359e4f97ada2563aaf1b435a6db13c4d4cb0ef375b2fbd93f7a73f6e8eeb229b`.
[Control composition regression](../../tests/GnOuGo.Flow.Planning.Tests/CoherentBusinessFragmentTests.cs)

Requests 35–37 traversed task pages at offsets 0, 8 and 16. Fresh drafts can
retrieve pages containing visible producers in one batch. The run also built
eight loops over the same source pages. Its grammar offered independent
extraction, but the projected contract had lost the explanation of how to
bind it. The fresh grammar describes the collection and result fields. The
controlled 52-page extraction test checks all 1,488 records in order and
retains repeated selections. [Navigation regressions](../../tests/GnOuGo.Flow.Planning.Tests/CompactDraftNavigationTests.cs) ·
[Extraction execution regression](../../tests/GnOuGo.Flow.Planning.Tests/CompactObservationTests.cs)

The financial audit recorded 54 measured HTTP attempts for the run and zero
unresolved attempts. Their estimated usage at captured tariffs was EUR
30.72961. Cumulative totals reached 2,674 attempts and EUR 1,189.20102 of
measured usage. Existing HTTP reservations of EUR 478.50045 and separate
application reservations of EUR 103.36148 left EUR 328.93705 available.

## Qualification at the previous EUR 1,700 ceiling, 20 November 2026

| Case | Consecutive verified Server executions | Last fresh session | Observed outcome |
| --- | ---: | --- | --- |
| Amazon first ten | 0 of 2 | `bc9cda6c8313437c8764099fef721b6f` | Search and cleanup reached technical validation. Execution never started. Zero business product visits and no XLSX. |
| Published review of PR 718 | 0 of 2 | `966fb4988be947c982be5e98bef6e28a` | Discovery stopped before a workflow could run. The checkout path was absent. GitHub returned zero reviews and zero diff comments. |

Qualification uses the saved workflow and the Server's typed chat inputs.
Amazon requires the ten observed product visits in order, the corresponding
rows in the reopened workbook and evidence that the browser closed. The
review takes the URL and review instructions at execution time and checks
the repository tests, GitHub verdict, inline diff comments and clone cleanup.
[Server execution](../../tests/GnOuGo.Agent.Planning.Benchmark/ServerBusinessExecution.cs) · [Amazon checks](../../tests/GnOuGo.Agent.Planning.Benchmark/LiveWorkflowOracles.cs) · [GitHub publication checks](../../tests/GnOuGo.Agent.Planning.Benchmark/PublishedReviewQualification.cs)

The review ran on `308a390e`. Its second model call selected the business
block directly from the seven displayed clauses. Six HTTP attempts returned
measured usage estimated at EUR 1.06981. The campaign refused discovery
request 7 before dispatch. It needed EUR 1.87224 with EUR 1.85913 available.
The preceding Amazon refusal needed EUR 3.07795 with EUR 2.92895 available.
Both test processes emitted warnings and exited zero with unqualified results.
Neither refused request issued an HTTP attempt.

GitHub still showed head `107c86fc87fcd89b529f6589736d27a09acb4206` when
checked after the review session. Its reviews and inline review comments
endpoints both returned empty lists. [Target PR 718](https://github.com/AxaFrance/SmartGuide/pull/718)

After these runs the campaign ledger recorded EUR 1,119.75822 of measured
usage at captured tariffs, EUR 478.50045 of unresolved HTTP reservations and
EUR 99.88219 of separately retained application reservations against its
EUR 1,700 ceiling. Source-only browser captures and controlled test executions
contributed no successful business runs to the table.

On `308a390e`, all 3,042 planning tests, 287 selected Server outcome tests,
41 Amazon verification tests and nine Document MCP structured-row tests
passed. The Release benchmark build reported zero warnings and zero errors.
The campaign admission and retention change passed 487 tests on `44d652fb`.
The preceding whole-solution run on `a91a86d3` passed 9,395 tests across
33 projects, with 13 skipped and zero failures.

## Fresh Amazon session bc9cda6c8313437c8764099fef721b6f, 20 November 2026

The real Server requested exactly ten products. It accepted a search task
and a browser cleanup task at its first technical checkpoint. Draft request
11 was refused before HTTP dispatch at EUR 1,700.14900 of current exposure
against the EUR 1,700 ceiling. Ten HTTP attempts had measured usage estimated
at EUR 3.18513. The test emitted a warning and exited zero with an unqualified
result. The workflow never executed and its requested workbook was absent.

An independent Browser MCP capture contained 1,593 source records in 77
pages. Its empty-ASIN context at record 429 also contained the bare deals
destination at record 433. The active grant did not authorize that
navigation. The verifier recorded zero source resolutions and zero business
product visits. Cold verification reproduced the result. The browser closed
and the second probe found no active page.

## Fresh review session c7b201b1b0ff4a04b4297176a768d8f6, 20 November 2026

Call 2 asked for the original request page. Four displayed requirements and
three displayed global constraints already contained all 1,321 characters.
Call 3 received that page as text. Fresh business selection identifies the
displayed rows in the original clause order when they reconstruct the entire
indexed page exactly. Replaying selection against the captured session with
the two builds confirmed that the redundant read disappeared and its seven
ordered rows reproduced the full text. Partial pages still offer retrieval.
[Source order regression](../../tests/GnOuGo.Flow.Planning.Tests/OverallProgressTests.cs)

Eleven HTTP attempts returned measured usage estimated at EUR 2.73275.
The campaign refused the first draft before its first HTTP attempt. Its
original record reported EUR 1,729.92444 against EUR 1,700. That figure
included EUR 28.93668 of future review forecast. Current exposure including
the requested reservation was EUR 1,700.98776. In experimental warning mode
the refusal now reports the amount the ceiling actually enforces. The
captured forecast remains separately available. The strict mode regression
still includes the forecast in its required amount. This run reached
neither approval nor execution. [Financial admission regression](../../tests/GnOuGo.Agent.Server.Tests/BenchmarkCampaignTests.cs)

The refused draft repeatedly embedded the same outer result constraints
across nine inspected schema paths. Two paths had a second ancestor, which
prevented the existing whole-list sharing from applying. Fresh presentation
shares the byte-identical common prefix and keeps each path's own suffix.
Replaying the actual refused request reduced its prompt from 122,151 to
31,663 UTF-8 bytes and its estimated input from 78,716 to 22,355 tokens.
All nine ancestor lists and selected schemas reconstructed exactly, including
the seven schemas linked to their original branches. The replay made zero
provider calls. [Mixed-depth regression](../../tests/GnOuGo.Flow.Planning.Tests/PlanningPromptPresentationTests.cs)

## Fresh review session b959c396c5914233abf87b90271346f3, 20 November 2026

Call 6 requested the named output port directory of an inspected operation
whose contract already showed an empty list and a count of zero. Call 7 received
the empty directory. Fresh discovery omits that link when the original contract
identity, hash and empty port list agree. The structural output schema remains
available, including all seven result alternatives for this GitHub operation.
Historical receipts still resolve the original port directory. The same
request also selected a 64-character operation description already displayed
in full in both the retained contract and the discovery list. Fresh discovery
omits that duplicate read when version, hash, complete original text and
displayed text agree. Explicit prerequisite citations retain access.

Call 13 had five available providers and three discovery focuses, all at their
initial cursor. Its eight displayed actions covered three providers. The Git
provider first appeared in the model's discovery requests on call 20. The planner
now offers all fifteen source/focus pairs when sharing descriptions keeps the
complete menu within the size of the original preview. Each action retains its
own source, focus, exact cursor and availability. A regression dispatches the
last provider with the selected prerequisite in one call.

At checkpoint three the generated plan contained one revision-pinned clone
and a root cleanup task. Call 30 then generated a comparison operation with
a conjunction asserting that both revision outputs were non-null. The compiler
reported `TASK_INPUT_TYPE` for `baseRef` and `headRef`. An offline replay of
that exact generated task against captured baseline
`e2f9c5348c55790bca8679f11083f49d835150de09c88cd38ddded1c5319db4e`
reproduced both diagnostics. With task-local non-null facts applied at required
operation arguments, the same replay returned zero diagnostics. Runtime tests
cover a successful pair, either value null and a disjunction that cannot prove
either value. On the earlier build the live planner added a validation transform
at call 34. These are planning observations.

Call 36 presented a required argument of an imported operation four times,
once at its original coordinate and once on each of three related inputs.
Fresh requests point these required siblings to the already displayed schema
when their contract, property coordinate and schema bytes agree. Replaying
the captured request reduced its UTF-8 prompt from 96,244 to 91,510 bytes
and its estimated input from 57,534 to 55,826 tokens. All three pointers
resolved to the original captured constraints.
[Presentation regression](../../tests/GnOuGo.Flow.Planning.Tests/PlanningPromptPresentationTests.cs)

Call 40 added a conditional that exported a second comparison page and a
boolean indicating whether it had run. Its continuation at the same scope
offered only value, transform and operation tasks. Call 42 requested another
selection. Calls 43–45 inspected and selected a sequence, and call 46 used both
conditional exports inside that sequence. A regression reproduced the
rejection by the original draft grammar. A different control can now consume
an exported port of the accepted control within the same business block.
The compiler still checks the actual reference, scope and selected operations.
Tests also cover an unrelated wrapper, a repeated wrapper and an open source.
[Construction regressions](../../tests/GnOuGo.Flow.Planning.Tests/CoherentBusinessFragmentTests.cs)

The earlier build continued reselecting pagination after it had retained
three page operations. The operator stopped generation at revision 175 after
the sixtieth response to run a fresh case on the fixes. Eight technical
checkpoints had been recorded. The 60 HTTP attempts cost EUR 31.17658 at the
captured tariffs, with zero outstanding reservations for this session. The
longest recorded model call took 71.53 seconds. The repository never reached
checkout or tests. GitHub still showed zero reviews and zero diff comments
on the 52-file PR at head `107c86fc87fcd89b529f6589736d27a09acb4206`.

The sixth independent Amazon source capture contained 1,642 records in
79 pages. Nineteen authorized link resolutions returned sixteen store pages
and three product links. The unclassified product context at record 497
also contained a bare `/deals` destination at record 503. The current
permission did not cover it. Cold verification reproduced the same findings.
The browser closed, the follow-up probe found no active page and the capture
recorded zero business product visits.

The seventh capture contained 1,663 records across 80 pages. Its empty-ASIN
context at record 506 contained the bare deals link at record 512. Three
product and sixteen store link resolutions were recorded. Cold verification
reproduced the source failure, the browser closed and its independent probe
found no active page.

## Fresh review session 553abdc993904ca0a052d6e38fa45223, 20 November 2026

Calls 10 and 12 inspected the Copilot and command-runner contracts. Call 17
requested the same two operation IDs and versions for another objective. The
Server reused the retained originals internally. The model still had to select
the inspection action to receive the two contract views again. Fresh discovery
now identifies exact, policy-allowed contracts on the displayed descriptor page.
It offers direct binding and a local evidence address for the original. Changed
or unresolved descriptors retain their provider inspection path.

The same run reached revision 119 with four accepted tasks for URL parsing, PR
identity, the diff and revision extraction. Its two caller inputs were
`pullRequestUrl` and `reviewText`. On call 32 the implementation response
selected deferred discovery with three revision imports. After opening the
mandatory empty `operationIds` array, it emitted 55,690 characters of whitespace.
The provider used all 4,096 output tokens, including 173 reasoning tokens. The
Server rejected the partial response. The 32 HTTP attempts had measured usage
estimated at EUR 15.95087 and no unresolved reservations for this session.
No workflow execution or publication took place. When fresh selection offers inspected operations, its deferred-discovery option
carries the same imports without spelling out an operation list. With no inspected
operations on offer, the local and deferred choices share one compact schema.
This keeps strict navigation admission within the existing 6,000-token allowance
in the suspended-consumer tests. Direct bindings still require IDs and exact contracts.

Replaying two oversized requests from `af621102` with the updated test
observations reproduced 6,217 implementation and 6,012 discovery tokens against
a target of 6,000. Both tests completed with warnings and reported the byte
counts by component. Strict admission and unfinished-response tests continue
to check the actual accepted or rejected state separately.

The fifth independent Amazon source capture returned 1,669 records. Twenty
authorized resolutions returned seventeen store pages and three product links.
The product links resolved to the same ASIN, but the independent verifier found the bare auxiliary /deals link at record 506 in the same card.
The active grant does not cover that bare destination. Cold reloading the
same evidence reproduced the findings and the independent browser closure probe
found no active page. Business product visits and workbook writes were zero.

## Fresh ten-product and published-review qualification, 19 November 2026

The ten-product Server attempt `bef26286efd94475bff02326756f3c60`
stopped during planning. Two fresh Browser MCP captures independently reproduced
the source prerequisite under the approved v10/v14 policy. Both reloaded with
identical findings and both closed the browser. The second MCP probe in each case
reported that there was no active page.

| Source capture | Ordered observations | Unclassified heading | Bare auxiliary link | Product visits |
| --- | ---: | ---: | ---: | ---: |
| First | 1,616 | 512 | 516 | 0 |
| Second | 1,623 | 518 | 522 | 0 |
| Third | 1,579 | None | None requiring proof | 0 |
| Fourth | 1,628 | 510 | 514 | 0 |

The empty-ASIN card in each of the first two captures contains an observed `/deals` link.
The active grant requires evidence for each unresolved occurrence across the
captured search. The independent verifier returned
`search_heading_context_unaccounted` and `source_resolutions_incomplete`.
Neither capture produced a workbook. The full Server attempt recorded 21 HTTP
attempts with measured usage, estimated at EUR 8.31965 using campaign tariffs.
Its separate application reservation of EUR 1.77437 remains held.
[Active workflow permission](pr120-aax-workflow-2026-11-12/README.md) ·
[Separate source-only experiment and inactive amendment](pr120-amazon-deals-proposal-2026-11-13/proposal.md)

At 22:38 UTC, fresh capture `amazon-ten-source-20261119-03` on
`b47d9afa` passed the same v10/v14 source verifier. Browser MCP retained
1,579 ordered records in 74 pages and completed 14 source resolutions.
Cold reload produced the same selection of ten expected products. Closing the
browser succeeded and the subsequent probe found no active page. This cleared
the source prerequisite for a new Server run. The only remaining finding in
that source-only capture was `first_product_visits_incomplete`.

At 00:23 UTC on 20 November, a fourth fresh capture on `ce736d34`
returned 80 pages. An empty-ASIN occurrence again contained the bare
`/deals` link outside the active workflow grant. The cold verifier
reproduced the source failure and the independent post-close probe found
no active page. The earlier qualifying capture illustrates why this
prerequisite must be evaluated on each actual search.

The review session `4c961e2da593431fb092672d3eb522e3` stopped after its eighth
successful planning response. It incurred EUR 2.05557 at campaign tariffs.
The live GitHub MCP returned the expected PR head and advertised message-only
responses for review writes. The verifier now reads back the authenticated
actor's newly created pending review ID and uses it for the submitted verdict
and inline comments. Discovery also exposed seven sibling branches duplicating
the same parent constraints. Remeasuring that exact captured request against
commit `a1bd411c` reduced the complete estimate from 66,206 to 28,602 input
tokens and the prompt from 112,579 to 52,006 UTF-8 bytes. All six references
resolved to the original constraints. This measurement used zero model calls.
No GitHub review was published by this session.
[Review read-back regressions](../../tests/GnOuGo.Agent.Server.Tests/PublishedReviewQualificationTests.cs) ·
[Planning prompt regressions](../../tests/GnOuGo.Flow.Planning.Tests/PlanningPromptPresentationTests.cs)


The next review session, `66b317828ad04e2293e1a347647a6d32`, used the production
Server with the 4,096-token planning allocations. The first two technical
checkpoints preserved the runtime URL, resolved PR revisions and review
instructions. The 49 received responses took at most 72.45 seconds each and
incurred EUR 21.10388 at campaign tariffs. The stop following response 49
raced with the Server reservation for request 50. Its EUR 1.76086 application
reservation is accounted for separately. The run ended before cloning or
publishing.

Three captured responses identify independently reproducible defects.

- Call 13 requested a Git catalogue page and an already listed operation's
  contract from the same provider. Batch validation treated them as duplicates.
  The regression distinguishes the two reads while preserving the rejection of
  genuinely overlapping requests.
- Call 19 repeated exact branches through their ancestor views. Sharing only
  byte-identical branches at the same contract coordinates reduced that request
  from 51,184 to 45,088 estimated input tokens and from 98,164 to 88,636
  prompt bytes. All six references resolved to the originals. The measurement
  used zero provider calls.
- Calls 34, 37 and 47 abandoned a selection around checkout and pagination.
  Git discovery now specifies when a full commit ID materializes the worktree.
  A real Git regression reopens all five changed files using the two remaining
  cursors returned for a two-file page. Larger directories preserve the
  existing single-cursor fallback.

Calls 35 and 38 also requested the same five-operation directory after a
change of control hid its fifth operation. Reselection now presents one
bounded, current page containing a previously bound operation from that
scope. The regression selects the fifth operation twice using just the two
selection calls. A change of scope or a policy denial still excludes it.

In fresh session `9390b48f84214ed28c0bafb72b0002c7`, call 9 abandoned a
read because it could not perform later business work. The captured draft
put the exact block objective and final expected results in the technical
step a second time, beside the narrower selected prerequisite. Drafting now
presents inherited goals once in the block and preserves independently
authored step objectives, results and prerequisite evidence. It can return a
partial executable contribution with the business obligations still open.

The same session's calls 18 and 20 displayed two of four valid focuses
for an already inspected provider. A focused regression reproduces the extra
read for the third focus. A small set of source actions with no continuation
cursor is now presented together, up to the existing eight-entry and
2,048-byte navigation bounds. Ordinary provider cursor pages retain their
versioned continuation. The session eventually accepted a two-task
contribution to parse the URL and read the PR. It stopped after 28 provider
responses, with a maximum recorded response time of 61.02 seconds and a
measured tariff estimate of EUR 12.27410. Request 29 retained a separate
EUR 4.97263 application reservation. No clone or GitHub review existed on
independent read-back after the stop.

Session `53d2b87b977d485b94b936360abc0da9` accepted three tasks in its
first checkpoint. They parse the caller's URL and retain the PR and diff
as separate observations. Re-rendering two captured requests produced the
following measurements without a provider call.

| Request | Duplicated content | Estimated input tokens before → after | Prompt bytes before → after |
| --- | --- | ---: | ---: |
| Draft 12 | Six selected branches already present at their original coordinates in the operation's shared union | 31,754 → 25,826 | 47,011 → 37,809 |
| Discovery 15 | Two distinct accepted results with an identical output type from the same operation version and port | 30,172 → 24,771 | 53,386 → 44,747 |

Every replacement pointer resolved to the exact original schema bytes.
The bound values and producer tasks remained distinct. On call 16 all five
provider actions and source description links were visible, yet the response
requested their directory. Calls 17–19 requested further provider or source
pages. Fresh diagnostic requests omit those directory reads when the entire
current menu is already present. The original description links and
incomplete directory continuations remain available.

In the same session, candidate 25 bound a nullable producer output to string
concatenation after explicitly asserting that the output was non-null.
Preflight reported `TASK_STRING_OPERATION_INVALID` at
`/tasks/clone_review_revision/inputs/remoteUrl`. The planner suspended
that binding and produced three additional tasks at call 29. String operations
can consume exact non-null facts established by their task's successful
`requires` assertion. Copying a nullable result retains its original contract
and needs no additional checked projection. Runtime regressions cover both caller inputs and
producer outputs. Text succeeds, null fails, and an unguarded use outside the
task still fails compilation. [Compiler regression](../../tests/GnOuGo.Flow.Planning.Tests/StringOperationCompilationTests.cs)

The local business judge accepted the evaluation block at call 42. At call 51
the planner abandoned the selected GitHub publication operations to find a
confirmation provider. The original qualification request had assigned that
responsibility to the workflow. The Server test host already displays the
concrete submission, inline comments and check receipts, records the operator's
answer and verifies the current GitHub head before forwarding the submission.
The fresh request now names that owner. The run stopped after 57 provider
responses, eight checkpoints and EUR 32.55524 of measured usage. The longest
observed provider wait was 69.98 seconds. GitHub returned zero reviews and
the clone path was absent. The terminal audit added no outstanding reservation
for this run. [Publication request regression](../../tests/GnOuGo.Agent.Server.Tests/LiveWorkflowReviewTests.cs)

[Discovery batch validation](../../tests/GnOuGo.Flow.Planning.Tests/CompactDiscoveryProtocolTests.cs) ·
[Git comparison regressions](../../tests/GnOuGo.Git.Mcp.Tests/GitRepositoryServiceTests.cs)


The second ten-product Server session, `f1c82849b1114de3aeedc04783e43ca5`,
drafted a model transform that would repeat every observed URL. The successful
source capture contained 835 links with 404,248 characters in their URLs alone.
The earlier review draft similarly requested the entire 247,256-byte PR diff
from a single interpretation. Draft and repair instructions now keep the original
payload at its producer, use typed bindings for lossless transfer and request
compact judgments or source positions from each model response. These sizes
describe the input evidence. Neither draft had executed the transform.
[Draft and repair regression](../../tests/GnOuGo.Flow.Planning.Tests/PlanningPromptPresentationTests.cs)

Call 32 accepted the source-selection block. Its business review showed the
same Browser operation description and ports twice. The two actions used
different arguments and scopes. Measuring the captured request with shared
contract details reduced the prompt from 49,887 to 43,802 bytes and its full
input estimate from 22,112 to 19,780 tokens. The presentation link resolved
to the exact original description and ports. The measurement made zero model
calls. Runtime coverage and the workbook still require execution.
[Local and global review regressions](../../tests/GnOuGo.Flow.Planning.Tests/BusinessReviewProtocolTests.cs)

The writer was present by checkpoint ten. Call 53 then abandoned the writer to
look for a delivery interface, even though the original asked for a file on disk
and declared zero caller outputs. Call 56 selected closure, and call 58 repeated
the delivery search with the same accepted plan. The implementation selection
could see the empty output directory. Its following draft and discovery requests
could not. Those requests now carry the same paged declarations or reuse their
existing evidence. Historical requests with an unknown output interface preserve
that distinction. [Output directory regressions](../../tests/GnOuGo.Flow.Planning.Tests/CompactContractEvidenceFlowTests.cs)

Call 67 inspected the Browser provider for a delivery consumer. The Server
policy now identifies a successful write at the requested path as file delivery,
keeps explicitly requested caller results and assigns execution status reporting
to the host. [Host policy regression](../../tests/GnOuGo.Agent.Server.Tests/Planning/AgentPlanningPolicyTests.cs)

The session stopped before approval at revision 178 with ten technical
checkpoints. It recorded 69 provider receipts costing EUR 37.40469 at campaign
tariffs. Request 70 had an application reservation but no campaign request,
HTTP attempt or receipt. Its entire EUR 1.97967 reservation remains held.
The 64-call threshold emitted warnings. The longest received call took 69.31
seconds. No workflow executed, product was visited or workbook produced by
this session. After retention the campaign had EUR 71.38389 available under
its EUR 1,700 ceiling.

The fresh PR 718 session `33c82d84e6e24e02a53272d2954195fe` reached six
checkpoints, including a pinned clone, separate Copilot checks and review, and
root cleanup. The diff remained bound to its original producer. On call 29 the
provider reported incomplete output at 4,096 tokens (97 reasoning tokens).
Its response stopped after the string item declaration of an array field,
followed by 7,453 whitespace characters. The strict transform grammar required
both array bounds even when neither was specified. Fresh drafts now also
accept an array with its typed items alone. Explicit bounds, including an
upper bound of zero, keep their original meaning. The incomplete response
was rejected. The run incurred EUR 15.40961 and ended before execution or
publication. [Array result contract regression](../../tests/GnOuGo.Flow.Planning.Tests/TaskTransformTests.cs)

## Execution baselines

The Amazon03 YAML matches the execution journal's `workflowYaml` byte for byte
(SHA-256 `9efd65d97d1af08e9f1088e261613a6fd6878a01151f62e9cf2e5aa3f4009d2b`).
Its `selected_product_items` TaskPlan value was already inlined. The workbook
formatter survived compilation as a template and an LLM step. Four runtime LLM
sites were present. One model call ran before permission denial stopped product
navigation. The formatter and writer were never reached. [Amazon03 execution record](server-independent-generation-2026-11-08/amazon03-execution-boundary.json)

Code23's retained session YAML has SHA-256
`dbd38653f7d0aa82b0a732e1fdab97529058b72d466cd41b35896793906d1f92`.
Its approval and execution identify artifact
`725650682b3420697a2a204b2df477037a1baa91d05d690add772d7603e68494`.
One clone completed. The first Copilot operation lacked a verified terminal
receipt, so cleanup remained blocked for reconciliation. The three coding
operations in the definition have separate purposes. [Code23 execution record](pr120-qualification-2026-11-11/code23-execution-failure.json)

## Before and after

| Operation | Before | Controlled result |
| --- | --- | --- |
| Document policy | One observed policy read in the older Amazon R9 run. Amazon03 already had zero. | A listed writer supplies its effective policy and accepts one direct row write with zero policy reads. |
| Workbook row preparation | Amazon03 has one formatting template and one formatting LLM site before the writer. | Ordered typed projections reach the sink in one draft. The emitted and executed fragment has zero formatting templates, inference or dynamic mappings. Real XLSX tests check headers, repeated rows, blank cells and exact text. |
| Git destination and cleanup | Code23 declares the same path literal in the clone and its cleanup. It already has one clone, zero preliminary folder creations and downstream bindings to `projectRootRelative`. | The shared-location regression checks one target declaration feeding clone and cleanup. Actual local Git execution checks the successful path, partial creation, cancellation, refusal and later failure. |
| Copilot sessions and contract lookup | Code23 has zero explicit session phases. Its `code_agent_edit` tool previously advertised the workspace input alone. | Both one-shot code tools advertise all four encapsulated phases. The scripted planner binds a cached complete operation once with zero provider pages or resolutions. Separate responsibilities keep their own calls. |
| Closing an accepted non-root scope | The ordinary close path uses one implementation selection and one draft. | Explicitly retaining existing exports uses one selection and zero drafts. Both paths preserve the same plan hash, add one technical checkpoint and leave business review pending. |

The typed-row and scope measurements use the normal planner with scripted model
responses. Workbook assertions reopen the file produced by the real Document MCP.
The historical Amazon03 and Code23 planning totals were 47 and 42. A comparison of
whole-run calls, latency and cost requires fresh live generation.

The direct-row tests exercise the mechanism. A particular workbook can also require
a specific missing-value label, which must be bound explicitly. Permission decisions,
source interpretation and business review remain part of the original outcomes.
[Earlier Amazon policy read](typed-bindings-live-2026-10-09/execution.md) ·
[Additional compiled Amazon workflow](amazon-fragment-qualification-2026-11-07/amazon07-generated-workflow.yaml)

## Regression entry points

- [Compact planner selection, drafting and row execution](../../tests/GnOuGo.Flow.Planning.Tests/CompactDeferredBindingTests.cs)
- [Scope closure, checkpoint provenance and business review](../../tests/GnOuGo.Flow.Planning.Tests/CompactAcceptedClosureTests.cs)
- [Document tool discovery and real row writes](../../tests/GnOuGo.Document.Mcp.Tests/StructuredRowsTests.cs)
- [Headers and exact XLSX cells](../../tests/GnOuGo.Agent.Server.Tests/LocalProductOutcomeExecutionTests.V10Rows.cs)
- [Local Git execution and failure cleanup](../../tests/GnOuGo.Agent.Server.Tests/RecordedCloneExecutionTests.cs)
- [Copilot discovery and composition over stdio](../../tests/GnOuGo.GithubCopilot.Mcp.Tests/CodeToolsStructuredOutputTests.cs)
