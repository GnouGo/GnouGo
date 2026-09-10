# Deterministic workflow planning

`GnOuGo.Flow.Planning` is the single Planner v2 implementation. It is a separately
publishable package depending only on Flow.Core. Concrete model and MCP clients
are supplied by integrations and hosts. Flow.Core owns provider-neutral contracts,
runtime validators, and the thin `workflow.plan` executor.

```mermaid
flowchart TD
  I[Intent] --> Q[Clarification when required]
  Q --> C[Capability discovery and locked contracts]
  C --> B[PlanningBehaviorPlan]
  B --> R[Deterministic validation and human behavior review]
  R --> G[PlanningGraph skeleton]
  G --> D[Resolve ownership, provenance, dataflow and dependencies]
  D --> H[Deterministic contract and binding propagation]
  H --> W[Fill ready typed holes, producers before consumers]
  W --> T[Typed validation and targeted repairs]
  T --> L[PlanningGraphCompiler]
  L --> Y[YAML]
  Y --> V[Compilation, semantic and scenario validation]
  V --> A[Exact revision and artifact approval]
```

## Session and construction

`IWorkflowPlanner.AdvanceAsync` advances one explicit session transition.
`TypedWorkflowPlanner` delegates intent assessment, capability preparation, behavior
review, dataflow resolution, workflow construction, repair acceptance and executable
validation to cohesive components. `PlanningGraph` is the authoritative executable
model. Intent, construction and validation state hold phase evidence and fingerprints.
YAML is a derived, read-only artifact produced by `PlanningGraphCompiler`.

Every model response has a strict typed JSON schema. Every classification and contract
must have deterministically validated evidence from intent, declared schemas or
provider-neutral metadata. Missing evidence requires eligible human clarification or
stops the session. Provider names, tool names and domain vocabulary never establish
execution authority. Saved YAML is imported once as a validated revision baseline;
capabilities are rediscovered before granting execution authority. Execution failure
evidence is kept separate from user intent.

Human acceptance of the exact business behavior precedes executable construction.
The skeleton inserts predefined pure decision-outcome and branch-result adapters
before freezing topology; call and loop projections are compiler-owned bindings.
Adapters have deterministic IDs and no capability or operation ownership. They
preserve no-action outcomes and original payloads. Explicit unresolved value and
schema descriptors prevent placeholders from becoming established contracts; the
compiler rejects any unresolved descriptor.
Dependencies include workflow calls inside branches, loops and finalizers. Missing
targets and cycles stop before dispatch. Each construction request fills coordinator-issued holes in one workflow. Schemas
resolve before bindings, and producers before consumers. Identity expressions retain
their existing typed bindings. An unambiguous direct consumer contract can refine an
open pure-producer result through a staged exact schema fragment, without a model call. The model cannot choose
workflow IDs, topology, executors, capabilities, call targets, branches or finalizers. Independent ready workflows run up to
the concurrency ceiling; callers wait for validated callee contracts. Workers receive
immutable requests, and the coordinator commits results in stable workflow order.

Requests contain only relevant accepted behavior, typed fields, native and capability
contracts, callee boundaries and diagnostics. Repeated contract objects are shared.
Construction and repair share the same scoped callee input/output boundaries;
callee implementations are excluded. Call and control-flow result contracts are
derived. Direct typed call references select a declared output port, while calls
inside collected loop results retain their runtime `outputs` envelope.
Raw YAML, unrelated workflows and previous attempts are excluded from construction.
Oversized requests and truncated responses pause with actionable diagnostics.

## Repairs and validation

One repair engine applies atomic patches to diagnosed typed fields. Assignment deltas are
staged against graph and dependency fingerprints and revalidated before commit.
Invalid candidates remain staged for repair; valid neighboring fields are retained. Global or unlocated findings grant no repair
scope. Accepted behavior, capability ownership, confirmations, finalizers, proven
provenance and validated contracts are preserved. Producer defects are repaired
before callers; changed producer contracts invalidate dependent callers.

Validation gates are typed contracts, deterministic lowering/runtime compilation,
scenarios, and semantic review. A repair must advance the first failing gate or
strictly reduce its required findings without adding failures at that gate. Earlier
passes and passing scenarios against unchanged fixtures must survive. No-op,
repeated and regressing candidates are rejected while retaining the previous graph.
Governing behavior or capability changes require renewed human review. Initial
behavior generation happens once. A human revision retains the candidate, locates
changes against exact excerpts of the revision, and stages patches to those fields.
Diagnosed structural corrections use explicit insertion, removal or move coordinates;
whole-plan and collection replacement are forbidden.
The accepted business projection supplies revision context without repeating the
baseline implementation. A reviewed input or output revision reopens that port's
schema; unrelated baseline contracts remain reusable. Semantic review sends exact
executable targets and routes changes to established contracts back through review.
Unchanged pure native producers can reuse baseline literal values after checking
their current contract, and derive literal result schemas without a model request.
Unreviewed candidates never become baseline intent evidence.

Repair response schemas enumerate opaque IDs for exact permitted fields and omit
unused definitions. Binding choices come from a scoped catalog; the coordinator
constructs references. Expressions declare their parameters. Unknown, duplicate,
overlapping and stale targets are rejected. Diagnostic identity uses the code,
canonical location and rule discriminator; diagnostic prose never defines progress. An output-value finding permits only that
value to change. Native argument diagnostics preserve unchanged members by name,
including when removing an invalid argument shifts their serialized positions.
Retrying a stopped repair revalidates the retained graph to recover its diagnostics;
it preserves the consumed allowance and durable requests.

Final approval targets the exact revision and artifact hash, after current-catalog,
compilation, semantic and scenario checks. Validation evidence is bound to graph,
locked-contract and fixture fingerprints. YAML cannot be edited during review.
Runtime expressions and WFScript remain supported, with their existing sandbox.

## Persistence, budgets and hosts

Agent.Server persists schema **4** snapshots in encrypted KeyVault records with
tenant-scoped EF Core/SQLite indexes. The default workspace-resolved database is
`.GnOuGo/data/gnougo-planning-v4.db`; snapshot, request, receipt and budget namespaces
are versioned together. Older storage is unused. Optimistic revisions, cancellation,
restart recovery, original-workflow save conflicts and human-wait accounting remain.

Each request has a durable identity covering session, revision, phase, workflow,
gate, hole scope, attempt and request hash. Repair allowances are consumed when
requests are reserved, persist across retries and restart, and never replace global budgets. Reservations are persisted before dispatch; completed encrypted receipts
are replayed after restart. Unverifiable dispatches stop without redispatch or budget
reset. Call, token, monetary, active-time and concurrency limits remain enforced.
Defaults are concurrency **4**, repair allowance **5 per workflow/gate** (configurable from 0–10 with `max_repairs_per_workflow_gate`), input ceiling **12,000** tokens
per request and output ceiling **8,192** tokens.

Agent.Server, Flow.Cli and Flow.Server inject the same planner and runtime factory.
A host that omits injection fails explicitly. `/gnougo add` and `/gnougo reprompt`
open the designer. SmartFlow **Improve** creates a persisted revision session from
the saved workflow and execution failure evidence, then links to the designer.
The designer reports resolved/unresolved field counts, the current gate and its consumed repair allowance.
Telemetry reports phases, workflows, dependencies, repairs, validations, budgets and
receipts with tenant propagation and content redaction.

## Validation commands

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet test tests/GnOuGo.Flow.Tests
dotnet test tests/GnOuGo.Agent.Server.Tests
dotnet build GnOuGo.Agent.sln -warnaserror
dotnet test GnOuGo.Agent.sln --no-build
dotnet pack src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -o /tmp/planning-smoke
/tmp/planning-smoke/GnOuGo.Flow.Planning.Smoke
```

The planning smoke uses native typed fixtures and exercises the published runtime.
See its README for narrowly scoped, dependency-specific Jint publish exceptions.
Agent.Server's published `--planning-persistence-smoke` exercises encrypted EF-backed
persistence, optimistic revisions and tenant isolation.

Standalone Flow runtimes use the same planner with
`GnOuGo.Flow.Integrations.Planning.WorkflowPlanningRuntimeFactory`. It holds an exclusive
tenant/session lease and persists encrypted snapshots, requests, receipts, and budgets through
KeyVault's record API. The CLI exposes `--run-id` to reopen planning state. Agent.Server's
designer keeps its tenant-scoped EF indexes and optimistic revisions. The Python runtime
executes saved workflows; planning is provided by the .NET hosts.
