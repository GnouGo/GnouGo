# Schema-9 workflow planning and execution

Flow uses one planner, one executable graph and one execution journal. Requirements
record the requested outcomes and acceptance criteria. Progressive capability
discovery selects declared operations; exact versioned contracts establish which
inputs and outputs can be used. The planner proposes `PlanningGraph`, deterministic
validators check it, and the compiler produces YAML for review and approval.

## Package boundaries

- **Flow.Core** owns the authored YAML runtime, graph and requirement contracts,
  `ICapabilityCatalog`, `IAgentTaskRunner`, `IAgentTaskVerifier` and `IWorkflowRunStore`.
  It references no other GnOuGo package.
- **Flow.Planning** implements `HybridWorkflowPlanner`, graph validation, bounded
  revisions and deterministic compilation. Its only GnOuGo dependency is Core.
- **Flow.Integrations** supplies AI and MCP transports and encrypted planning
  sessions through injected interfaces.
- **Flow.Copilot** implements the agent runner over injected MCP transport. Its only
  GnOuGo dependency is Core. The MCP host uses the existing managed Copilot APIs.
- **Flow.Persistence** stores authoritative execution payloads through KeyVault's
  encrypted record API and rebuildable tenant indexes through EF Core and SQLite.
- **Agent.Server**, **Flow.Server** and **Flow.Cli** compose these packages.

Ordinary MCP workflows require no Copilot adapter. Git MCP remains available for
structured repository preparation and exact revision comparison; adaptive editing,
testing and routine commands belong inside an approved agent task.

## Planning and approval

`ICapabilityCatalog.ListSourcesAsync`, `ListAsync` and `ResolveAsync` separate source
summaries, paginated operation summaries and exact contracts. Cached pages and
versions survive repairs. Unavailable and uninspected sources remain visible as
discovery limitations; unrelated unavailable sources do not stop a valid plan.
Selected contracts are checked again before approval and execution.

Requirements are reviewable intent, not another executable program. Outcome descriptions remain stable throughout technical repairs. New generated glue permits literals, typed references,
simple conditions and registered typed transformations. Authored YAML retains its
existing expression runtime. Opaque output needs whole-value validation before
field access; assistant descriptions and sample values cannot establish a contract.

A failed validation opens one bounded revision scope. Dependent findings are
invalidated; unaffected validated stages and interfaces remain unchanged. Repairs,
retries and restarts share the original planning budget. Defaults remain eight
model calls, two repairs, 12,000 input tokens per request and the configured output
ceiling. A host may explicitly configure a larger request limit before dispatch.

Approval identifies the exact requirements, graph, compiled artifact, selected
contracts, task scopes, verification requirements and budget ceilings. A change to
that scope requires a fresh review. Artifact approval does not replace the host's
existing runtime permission decisions. Static validation and simulations are
labelled separately from observed external execution.

## Bounded agents

`agent.run` accepts `runner`, `objective`, `inputs`, `output_schema`, `workspace`,
`capabilities`, `budget` and `verification`. Budget fields are
`max_elapsed_milliseconds`, `max_model_calls` and `max_total_tokens`. A verification
requirement supplies `id`, `kind`, `subject` and `facts_schema`.

The runner enforces the approved scope and returns separate status, structured
output, artifacts, observed evidence, usage and verification findings. Core checks
the output contract and every required finding before downstream execution. A
completed assistant turn or a claim that tests passed does not establish success.

The Copilot adapter declares `project.read`, `project.write`, `command.execute`,
`command.exit` and `file.content` outside Core. Commands require the host's mandatory
sandbox; interactive permission refusals remain effective. Unsupported inference
transports fail closed. Conservative non-refundable reservations are reported as
`reserved_upper_bound`, not as measured token use. See the [adapter README](../src/GnOuGo.Flow.Copilot/README.md).

## Durable runs

Invocation identities include workflow call path, branch, loop iteration and step.
The encrypted journal records intent before dispatch and completion receipts after
execution, along with control state, resolved inputs, outputs, pending human input,
budgets and finalization progress. Recovery reuses completed receipts.

An interrupted external effect without a receipt enters `needs_reconciliation`.
The agent adapter may inspect the original invocation without dispatching it again.
If its outcome remains unknown, an operator must establish that it stopped before
marking it failed. Cleanup cannot race unresolved active work. Copilot's internal
session is not automatically restored after a process crash.

Owner locks prevent concurrent execution; short write locks and revisions serialize
commands. Every record belongs to a tenant. Answer persistence precedes acknowledgement,
including MCP permission dialogs. Distinct dialog identities prevent a prior answer
from authorizing a later request.

Hosts sharing a KeyVault must share `Flow:Execution:OwnerPath`. Optional configuration:

```json
{
  "KeyVault": { "DatabasePath": "/deployment/vault.db" },
  "Flow": {
    "Execution": { "IndexPath": "/deployment/run-index.db", "OwnerPath": "/deployment/run-owners" },
    "Planning": { "OwnerPath": "/deployment/planning-owners" },
    "CopilotRunners": { "coding": "configured-copilot-mcp-server" }
  }
}
```

Omitted paths use workspace helpers. The index contains metadata, not workflow
payloads. Deleting the index does not delete authoritative encrypted runs.

## HTTP and CLI commands

For the configured tenant, `/api/tenants/{tenantId}/runs` lists runs and `/{runId}`
inspects them. Commands use `POST /{runId}/resume`, `/cancel` or `/reconcile` with
`{"expectedRevision": N}`. Reconciliation also supplies `invocationId`; an optional
`confirmedStoppedReason` records explicit confirmation that the operation stopped
without a trustworthy result. It cannot manufacture a successful receipt.

`POST /{runId}/human-input` accepts
`{"expectedRevision": N, "invocationId": "...", "response": ...}`.
Stale revisions return conflict. Cross-tenant run access is rejected.

Python consumers use `gnougo_flow_core.WorkflowRunClient` or `gnougo-flow-cli runs
--server URL --tenant TENANT` for these same commands. The Python demo's top-level
in-memory checkpoint model, store and local resume path are removed. Its local YAML
runtime remains available for authored workflows; schema-9 durability and bounded
agent execution belong to the shared .NET host. The HTTP client validates schema
and ownership and does not retry commands or follow redirects. After a transport
failure, inspect the stored revision and recovery status before issuing a command.

```sh
gnougo-flow runs --tenant default --id RUN_ID
gnougo-flow run workflow.yaml --run-id RUN_ID --resume-revision REVISION
gnougo-flow runs --tenant default --id RUN_ID --command cancel --revision REVISION
gnougo-flow runs --tenant default --id RUN_ID --command reconcile --revision REVISION --invocation INVOCATION_ID
```

## Migration from schema 8

1. Upgrade the host and all affected Flow consumers together. There are no old
   checkpoint routes, compatibility DTOs or legacy planner switch.
2. Keep existing encrypted records. No in-place conversion or deletion is performed.
3. Regenerate the workflow through the new planner and review its requirements,
   stages, contracts, evidence requirements and budgets. Approve the new artifact.
4. Start a new schema-9 run. Old approvals and checkpoints cannot authorize execution
   or resume; incompatible sessions instruct the user to regenerate and approve.
5. Independently authored YAML remains supported by the runtime language. It may
   start a new run under the host's normal authorization; it does not inherit a
   previous planning approval.

Public planning consumers use requirements, stages, discovery limitations and
validation results. Chat and Designer expose agent stages and verification; execution
views expose invocation receipts, budgets, human waits and recovery state.

See [implementation evidence and deleted subsystems](flow-hybrid-v9-implementation.md).
