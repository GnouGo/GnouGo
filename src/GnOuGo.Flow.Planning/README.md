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

Source authority comes from owned metadata. User requests and answers can introduce
actions; existing actions require issued baseline nodes. Policy subjects cannot become
operations. Complete clauses adjudicate preliminary permission labels together;
rejection always prevents the protected action. See [source grounding and scoped
confirmation](../../docs/planner-confirmation-scopes.md).

Runtime actions pass [evidence-based admission](../../docs/planner-runtime-admission.md). Source interpretation independently distinguishes planning directives, public contracts, local executable behavior and requested external actions. Canonical declarations resolve first. Directives, declarations, omission defaults and policy subjects produce no operation question. Interpretation supplies action or governing evidence, never an operation subject or canonical occurrence. Within `intent_operations`, unopposed actions establish roots deterministically; compatible action candidates use bounded identity-only decisions (`distinct`, `same_as`, retirement or unresolved). Governing evidence attaches only after roots exist, deterministically for a unique compatible root or through a validated canonical target set for shared/ambiguous scope. Local operations retain native nodes without external capability authority or capability-matching calls. Admission commits atomically before confirmation scoping and inventory. Neither proven local behavior nor an external action means `INTENT_OPERATION_UNRESOLVED`. ConstraintsOnly runtime policy scope is engine-owned and absent from model answers. Exact canonical declaration evidence excludes covered local occurrences without discarding independently evidenced actions in a shared clause. Runtime proof version 4 and operation proof version 5 are additive within Schema-5; missing historical proof requires explicit reassessment.

Runtime parsing collapses exact duplicate normalized evidence in first-seen order,
without a model correction or repair charge. It compares every record field because
stable evidence IDs omit some contract metadata. Colliding IDs with different
requiredness, baseline authority or resource semantics stop with the located
`INTENT_RUNTIME_EVIDENCE_CONFLICT` diagnostic. Canonical IDs, fingerprints and the
original model receipts remain unchanged by repeated identical entries.

Public inputs and outputs first pass [canonical declaration adjudication](../../docs/planner-business-declarations.md). Interpretation emits direction-neutral candidates. Existing adjudication pages establish canonical roots with explicit direction/presence before resolving aliases and modifiers against those roots; preliminary spans cannot create ports. Only `omission_default` modifiers can supply optional input defaults; `runtime_fallback` stays executable evidence. Output defaults, unspecified distinct-port presence and changes to established direction/presence through attachments are excluded by the response schema.

`declaration_constraint` evidence enters the existing attachment pages directly for
member types, enums, nullability and preservation. It must attach as `modifier_of`
an established declaration or stop unresolved. It cannot create ports or supply
defaults. Actual `explicit_value` obligations remain separate. Source proof 4 and
declaration proof 5 retain the existing Schema-5 storage boundary. Canonical port
IDs depend only on direction, scope and exact name. Compatible duplicate roots get
one correction restricted to their conflicting candidates, with other roots frozen.
Original decision/evidence lineage prevents another correction or output escalation
through regrouping; incompatible contracts still stop before review.

Initial behavior is assembled from owned operations, canonical declarations and bounded boundary/iteration
choices. The executable skeleton freezes identities, topology, executors, ownership,
branches, finalizers and established contracts after human acceptance. Callees precede
callers; independent workflows execute concurrently and commit in stable order.
One eligibility analysis controls deterministic binding resolution and model exposure.
Unknown compatibility never authorizes a direct binding. Contracts propagate in both
directions without rewriting locked constraints or inventing opaque external results.

All model requests pass the same central sizing boundary, including instructions,
context, response schema and estimated transport overhead. Pages target 80% of the
configured input ceiling (9,600 of 12,000 tokens by default) and structured answers
of at most 2,048 estimated tokens. Normal requests retain the 8,192 output ceiling. Complete
catalog and evidence coverage does not depend on a single prompt. An indivisible
oversized decision stops with a technical diagnostic; it does not raise limits or
ask the user to solve a transport limitation.

Repairs use reference-keyed field assignments. The coordinator constructs the atomic
patch, retains valid neighbors and rejects unknown, overlapping, unlocated, stale,
repeated or regressing changes. One unchanged decision/evidence pair permits at most
one model correction, within five repairs per workflow/gate. A verified truncated
multi-decision page partitions recursively in canonical order until each response
completes or a single decision exhausts its bounded allowance. Partitions preserve the
parent's gate and restricted correction scope, consuming global model budgets but
no semantic correction allowance. Persisted child identities and receipts retain
completed siblings across restart. A verified singleton truncation at exactly 8,192
permits one identical request at 16,384, with unchanged reasoning. This output-budget
escalation is shared by canonical decision and evidence across revisions and
corrections. Both host journals require the exact owned parent request and durable
truncation receipt. Escalation consumes global budgets, never a semantic repair
allowance; a second truncation stops. Other configured ceilings do not escalate.
See [bounded output escalation](../../docs/planner-output-budget-escalation.md).
Unverifiable
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
Clarification is a last resort after preparation and deterministic behavior resolution.
Source fragments are checked in their complete clauses; runtime conditions, input
defaults and confirmations remain executable behavior. Eligibility and governing
references control automatic choices and the two-to-four labeled suggestions. A
preferred suggestion requires declared evidence and is never preselected. Typed
answers and bounded custom-answer decisions retain receipts and correction allowances.
See [clarification](../../docs/planner-clarification-resolution.md).

Confirmation policies govern issued actions and interaction subjects. Different scopes
do not conflict or disable each other's permission requirements. The coordinator locks
scoped permission sources and constructs their guards before topology freezes; the
compiler rejects effects outside those guards. See [confirmation scopes](../../docs/planner-confirmation-scopes.md).

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

Occurrence identity excludes necessity. Interpretation records `required`, `optional` or `unspecified` with an owned source reference for explicit claims. Canonical admission resolves the combined necessity evidence: unspecified requested actions default to required; explicit optionality is retained; contradictory explicit claims stop. Conditions never make an implementing capability optional. Baseline nodes retain their exact execution authority. See [operation necessity validation](../../docs/planner-operation-necessity.md).
