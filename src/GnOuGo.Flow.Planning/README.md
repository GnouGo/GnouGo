# GnOuGo.Flow.Planning

An independently publishable Planner v2, depending only on Flow.Core. Hosts inject
model/MCP transports, runtime validation and encrypted persistence through
`IPlanningRuntime`; `IWorkflowPlanner.AdvanceAsync` remains the session boundary.

The production path is intent interpretation → discovery and locked capabilities →
coordinator-built business behavior → human review → deterministic graph skeleton →
contract/dataflow propagation → unresolved typed assignments → exact repairs →
`PlanningGraphCompiler` → compilation, scenarios and semantic review → exact-hash
approval. `PlanningGraph` is the authoritative executable model. Models never
produce YAML or patch lowered artifacts.

The engine issues references to immutable source spans, declared contracts and
editable coordinates. Responses select those IDs or supply new semantic values;
known quotations, schemas, workflow identities and capability bodies are not model
output. Capability assessment covers the catalog through obligation/candidate pages.
Descriptions cannot establish execution-context or original-artifact proof. Intrinsic
operations, compiler responsibilities and runtime discovery remain distinct.

Initial behavior is assembled from owned operations and bounded boundary/iteration
choices. The executable skeleton freezes identities, topology, executors, ownership,
branches, finalizers and established contracts after human acceptance. Callees precede
callers; independent workflows execute concurrently and commit in stable order.
One eligibility analysis controls deterministic binding resolution and model exposure.
Unknown compatibility never authorizes a direct binding. Contracts propagate in both
directions without rewriting locked constraints or inventing opaque external results.

All model requests pass the same central sizing boundary, including instructions,
context, response schema and estimated transport overhead. Pages target 80% of the
configured input ceiling (9,600 of 12,000 tokens by default) and structured answers
of at most 2,048 estimated tokens. The hard output ceiling remains 8,192. Complete
catalog and evidence coverage does not depend on a single prompt. An indivisible
oversized decision stops with a technical diagnostic; it does not raise limits or
ask the user to solve a transport limitation.

Repairs use reference-keyed field assignments. The coordinator constructs the atomic
patch, retains valid neighbors and rejects unknown, overlapping, unlocated, stale,
repeated or regressing changes. One unchanged decision/evidence pair permits at most
one model correction, within five repairs per workflow/gate. A verified truncated
multi-decision page may split once into charged correction pages. Unverifiable
requests are never redispatched. Previously passing validation stages and scenarios
must remain passing against unchanged fixtures.

Phase reasoning defaults to `medium` for behavior decisions/revisions and semantic
review, and `low` otherwise. Provider-neutral metadata must establish support before
dispatch; the effective reasoning and metadata fingerprint persist with the request.
Concurrency four and configured global call/token/cost/active-time budgets remain
unchanged. Reservation and correction accounting survive revisions and restart.

Schema 5 persists reference indexes, decision pages, staged deltas, exact request
manifests and receipts. Agent.Server uses the workspace-resolved
`.GnOuGo/data/gnougo-planning-v5.db` with encrypted schema-5 record namespaces.
Schema-4 storage remains untouched and unused by production; historical audit tooling
runs the pinned historical decoder separately. Tenant isolation, optimistic revisions,
cancellation, human-wait accounting and approval/save conflict checks remain mandatory.

Typed outcomes distinguish `ValidWorkflow` (only after exact-hash approval),
`NeedUserClarification` (a missing business decision) and proven `Unsupported`.
`FinalReview` is a waiting state. Provider failures, unverifiable dispatches, budget
exhaustion and invalid output have separate technical status and cannot claim business
unsupportedness. Clarification answers continue the same session with cumulative budgets.

Progress and redacted telemetry expose pages, utilization, holes, resolution origin,
request reasons, receipt evidence and calls/tokens/repairs/failures by workflow and gate.
Missing usage stays unknown. Detailed request and hole IDs belong in traces, not metric
dimensions. Mandatory validation is attributed separately from executable ambiguity.

```sh
dotnet build src/GnOuGo.Flow.Planning
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke
```

See [architecture](../../docs/workflow-planning-v2.md),
[convergence evidence](../../docs/planner-engine-owned-decisions.md) and
[published smoke](../../tests/GnOuGo.Flow.Planning.Smoke/README.md).
