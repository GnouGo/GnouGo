# TaskPlan planning format 11 and execution schema 9

One incremental TaskPlan is authoritative. Existing symbols, contract validation and
compiler v15 provide technical guarantees; local/global business Judges assess meaning;
independent runtime oracles establish actual execution success. There is no second plan,
execution engine or review-manifest protocol.

## Package boundaries

- **Flow.Core** owns the authored YAML runtime, TaskPlan, graph and requirement contracts,
  `ICapabilityCatalog`, `IAgentTaskRunner`, `IAgentTaskVerifier` and `IWorkflowRunStore`.
  It references no other GnOuGo package.
- **Flow.Planning** implements `HybridWorkflowPlanner`, task compilation, shared graph validation, bounded
  semantic revisions and deterministic compilation. Its only GnOuGo dependency is Core.
- **Flow.Integrations** supplies AI and MCP transports and encrypted planning
  sessions through injected interfaces.
- **Flow.Core** also supplies the native business runner. It uses the configured
  workflow model and the explicitly granted MCP methods.
- **Flow.Copilot** implements code review and editing through the managed Copilot
  SDK. The separately published Copilot MCP service shares its bounded task service.
- **Flow.Persistence** stores authoritative execution payloads through KeyVault's
  encrypted record API and rebuildable tenant indexes through EF Core and SQLite.
- **Agent.Server**, **Flow.Server** and **Flow.Cli** compose these packages.

Keep known operations in the workflow, including checkout, exact revision comparisons,
known checks, file writes and independent readback. Compose them with conditions
and loops. Use a bounded `llm.call` to interpret supplied data. Use `agent.run`
when an observation determines which action to take next.

## Planning, review and approval

Retain authoritative originals and answers encrypted with tenant ownership. Index
sources and typed public interfaces atomically in one pass for short requests, then
select the immediate business contribution, discover its unknown capabilities and
draft a bounded extension of related tasks. Measure complete requests before paging
large originals; source clauses are acceptance criteria, never required task boundaries. Reuse available producers and
inspected contracts. Concrete bindings must validate before checkpoint acceptance.

Generation and review have independent granularity. Accumulate coherent business work
across fragments. Purely mechanical work does not issue Judge calls. Review completed
business boundaries when planning continues, and always review the completed workflow
globally before approval. Finalization absorbs a pending local review; a small workflow
can receive one global review alone. All original obligations apply globally.

Present actual behavior and necessary original MCP meaning, with targeted evidence
retrieval. Keep schemas and receipt/identity verification in program code. The Judge
returns a strict short `ok`, `needs_change` or `needs_context` decision. Missing evidence
is not a proved defect. Context retrieval and supported interpretation correction use
existing bounded accounting without forced artificial edits or a contestation protocol.

Use `array_length` on a nonnullable array to compare its observed count in a
predicate. For example, a guard can count the ordered rows exported by a
`foreach` before writing them. Repeated elements each count once. A nullable
array can be checked in the right operand of a short-circuit presence guard.
For a nullable numeric field, `field != null && field > 0` checks presence
before ordering. Inside a nested `foreach`, the inner item needs its own
presence check.

Typed repair preserves requirements, effects, executable literals and unrelated work.
If rollback removes a child and reopens its original scope, the block that
created the surviving container can continue rebuilding in that scope.
The invalidated fragment still needs business review.
Explicit accepted-work revision invalidates affected approval. Code checks contracts,
references, scope isolation, dependencies, permission/provenance, final graph and YAML.
A favorable Judge or successful compilation never grants execution authority.

Artifact approval binds the exact current artifact and complete business review.
Production human approval and isolated delegated-test approval remain distinct. Individual
runtime confirmations bind the exact invocation; broader grants are not inferred.

## Bounded agents

`agent.run` accepts `runner`, `objective`, `inputs`, `output_schema`, `workspace`,
`capabilities`, `budget` and `verification`. Budget fields are
`max_elapsed_milliseconds`, `max_model_calls`, `max_total_tokens` and the action
limit `max_actions` required by the native business runner. A verification
requirement supplies `id`, `kind`, `subject` and `facts_schema`.

The runner enforces the approved scope and returns separate status, structured
output, artifacts, observed evidence, usage and verification findings. Core checks
the output contract and every required finding before downstream execution. A
completed assistant turn or a claim that tests passed does not establish success.

The host binds each runner to a profile and validates requested capabilities before
execution. The native runner can call only approved, version-pinned business tools.
Both runners page large incoming JSON inputs. The SDK's task-local input reader
can read only that invocation's supplied data. Input pages carry no verification
authority. The native runner also pages tool results while retaining complete receipts.

The code adapter exposes approved project reads, sandboxed native search, edits
and commands. It verifies `file.read`, `file.content`, `command.exit` and
`review.diff` against recorded actions and workspace evidence. Conservative usage
reservations carry the label `reserved_upper_bound`. Details are in the
[adapter README](../src/GnOuGo.Flow.Copilot/README.md).

The read-only SlimFaas qualification checks the pinned complete Git comparison, runs the upstream annotation cleanup unit tests through a named Cmd operation, and passes the evidence to the shared code runner. Its independent verifier compares the public report to the written JSON and reconciles coverage with recorded SDK reads.

When reviewed, reachable contributions cover every outstanding requirement and the
existing technical checks pass, the host requests global review directly. The
global review, artifact approval and execution checks still run in order.

## Durable runs

Invocation identities include workflow call path, branch, loop iteration and step.
The encrypted journal records intent before dispatch and completion receipts after
execution, along with control state, resolved inputs, outputs, pending human input,
budgets and finalization progress. Recovery reuses completed receipts.

New runs use a versioned private split layout in Flow.Persistence: immutable,
tenant/run-owned blocks are written before publishing an authoritative checkpoint.
Repeated observation subtrees are shared. Incremental owner saves and cancellation
polling avoid full-run reads; the public schema-9 inspection/recovery result stays
complete. Historical monolithic runs remain unchanged. See the
[persistence contract and rollout](../src/GnOuGo.Flow.Persistence/README.md).

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

## Current storage and recovery

Planning format **11** is the only active internal protocol. Old planning formats,
presentation revisions, request adapters and revise-to-upgrade behavior are removed.
Reject obsolete records before deserialization and leave their ciphertext unchanged.
Fresh generation requires fresh approval; old approvals do not transfer. Existing
execution journals remain schema **9**, and JavaScript → YAML **v15** is unchanged.

Current sessions resume exact issued requests/receipts and cumulative reservations.
Unknown outcomes stop without replay. Confirmed eligible HTTP failures use only the
captured journaled retry policy. Financial inspection and permanent abandonment records
remain historical data independent of retired planning engines.

## References

- [Construction and business review](incremental-taskplan-construction.md)
- [Compiler and repair](../src/GnOuGo.Flow.Planning/README.md)
- [Runtime mappings](runtime-mappings.md)
- [Benchmark operation](../tests/GnOuGo.Agent.Planning.Benchmark/README.md)

Historical source-bound results remain under [evidence](evidence/compact-taskplan-convergence.md).
They are never attributed to later source or used as proof of live convergence.
