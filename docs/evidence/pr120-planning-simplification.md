# PR 120 planning simplification

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
