# GitHub mutations through configured MCP workflows

Agent.Server discovers and executes the configured official GitHub MCP through its
normal MCP runtime. The server's tools, schemas, annotations and host configuration
determine the available capabilities. There is no GitHub-specific tool filter,
virtual publication server or publication service in Agent.Server.

The planner remains provider-neutral. It validates declared capabilities, preserves
requested business outcomes and requires an explicit scoped revision when an
outcome is unsupported. Availability of inline comments or other mutations depends
on the configured MCP catalog and authorization; descriptions alone do not prove
that an operation satisfies a requested outcome.

FinalReview and hash-bound artifact approval remain separate from execution.
Planned workflows with write, execute, lifecycle or unknown effects retain the
generic runtime confirmation guard. Refused, cancelled or unavailable confirmation
prevents the protected workflow effects. MCP elicitation and runtime `human.input`
retain their existing routing, permissions and execution correlation.

## Breaking changes

- Agent.Server no longer provides `GnOuGo.Review`, `review_evaluate`, `review_publish`
  or the `ReviewPublication` settings section. Remove obsolete configuration.
- Core's `ReviewEvaluation`, its evaluation/check contracts and publication verdict
  enum are removed. Copilot review analysis, finding validation, coverage and original
  SDK execution observations remain available.
- Stored drafts, evidence interception, provider-specific confirmation, fresh-head
  checks, publication locking and uncertain-publication replay are removed. Generic
  MCP execution does not inherit those publication-specific guarantees. Any required
  business checks must be represented explicitly in the workflow using declared MCP
  capabilities and normal workflow validation.
- Existing workflows and saved plans referencing the removed virtual capabilities
  require replanning against the current catalog and renewed artifact approval.
  Catalog revalidation rejects obsolete capabilities. No compatibility tools,
  automatic substitutions, migration or replay are provided.
- Existing encrypted review records are left untouched. The runtime no longer reads
  or writes them. Encrypted planning persistence and its tenant isolation are unchanged.

Historical verification reports retain their original observations and carry a
notice where they describe the removed subsystem.
