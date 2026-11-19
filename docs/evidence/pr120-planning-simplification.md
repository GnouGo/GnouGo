# PR 120 planning simplification

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
