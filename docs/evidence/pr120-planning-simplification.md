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

The empty-ASIN card in each capture contains an observed `/deals` link.
The active grant requires evidence for each unresolved occurrence across the
captured search. The independent verifier returned
`search_heading_context_unaccounted` and `source_resolutions_incomplete`.
Neither capture produced a workbook. The full Server attempt recorded 21 HTTP
attempts with measured usage, estimated at EUR 8.31965 using campaign tariffs.
Its separate application reservation of EUR 1.77437 remains held.
[Active workflow permission](pr120-aax-workflow-2026-11-12/README.md) ·
[Separate source-only experiment and inactive amendment](pr120-amazon-deals-proposal-2026-11-13/proposal.md)

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

[Discovery batch validation](../../tests/GnOuGo.Flow.Planning.Tests/CompactDiscoveryProtocolTests.cs) ·
[Git comparison regressions](../../tests/GnOuGo.Git.Mcp.Tests/GitRepositoryServiceTests.cs)


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
