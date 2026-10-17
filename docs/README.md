# Documentation

Start with the guides below for current architecture, behavior and configuration.
Component-specific setup, APIs and validation commands live in the corresponding
[`src`](../src/) and [`tests`](../tests/) READMEs.

## Current guides

| Topic | Guide |
| --- | --- |
| Planning, compilation, approval and compatibility | [Workflow planning](workflow-planning-v9.md) |
| Business requirements and clarification | [Planning clarification](planning-clarification.md) |
| Compiler lowering and historical profiles | [Compact workflow bindings](compact-workflow-bindings.md) |
| Extraction, mapping budgets and recovery | [Runtime mappings](runtime-mappings.md) |
| Runtime primitives and migration | [Flow runtime primitives](flow-runtime-primitives.md) and [numeric compilation](numeric-compilation.md) |
| Workspace ownership and operation contracts | [Workspace contracts](workspace-contracts.md) |
| Permissions and workflow visualization | [Agent permissions and animation](agent-permissions-and-animation.md) |
| GitHub operations and approvals | [GitHub MCP workflow execution](github-mcp-workflow-execution.md) |
| Protocol diagnostics and trace retention | [LLM protocol and traces](llm-protocol-and-traces.md) |
| Execution budgets and uncertain outcomes | [Agent budget interruptions](agent-budget-interruptions.md) |

Follow the repository [contribution instructions](../AGENTS.md) and their linked
skills when changing these components.

## Historical reports and evidence

Dated reports and [`evidence/`](evidence/) record observations against particular
revisions. Their commands, approval records, counts and limitations describe those
runs; use the current guides and component READMEs for present behavior.

Some older files remain necessary for regression tests, compatibility explanations
or investigation. Examples include:

- [Retained mapping responses](evidence/id-selection-lookup-2026-10-08/runtime-mapping-responses.json), embedded by the Flow regression tests.
- [Browser acquisition diagnostics](evidence/browser-acquisition-diagnostics-2026-10-09/README.md), whose intermittent timeout was not reproduced in that investigation.
- [Test-suite interruption evidence](evidence/browser-empty-acquisition-2026-10-08/README.md) and the [subsequent listener-ownership correction](evidence/consumer-bindings-2026-10-09/README.md).
- [Repair-schema preflight failure](evidence/consumer-input-flatten-live-2026-10-08/results.md) and [journal-inspection memory limitation](evidence/mapping-lifecycle-2026-10-08/README.md), retained with their supporting artifacts.

## Cleanup policy

The 17 October 2026 cleanup reviewed historical material dated before 10 October.
Seven days was a review threshold, not an automatic expiry rule. Current guidance,
test resources, referenced artifacts, unresolved diagnostics and uncertain cases
were retained, including older folders containing newer reports.

For future cleanup, check incoming references from documentation, skills, project
files, scripts and tests, including the dependencies of retained reports. Review
the contents before deleting a report and its evidence. Use report dates rather
than checkout timestamps or bulk Git modification dates.

Removed reports remain recoverable from Git history. The snapshot immediately
before this cleanup is `66bcd73280e75b53ef574d01fb2f611ac6e213af`; inspect a removed
file with `git show 66bcd732:<original-path>`, using its repository-relative path.
This cleanup does not schedule automatic deletion or rewrite Git history.
