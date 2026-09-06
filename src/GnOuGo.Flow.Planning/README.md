# GnOuGo.Flow.Planning

Separately publishable typed workflow planner. Depends only on the public contracts
in `GnOuGo.Flow.Core`; AI, MCP, storage and user interfaces are injected by hosts.

```sh
dotnet build src/GnOuGo.Flow.Planning/GnOuGo.Flow.Planning.csproj
dotnet test tests/GnOuGo.Flow.Planning.Tests/GnOuGo.Flow.Planning.Tests.csproj
```

The planner advances an encrypted host-owned `PlanningSnapshot` one phase at a time.
Behavior review precedes generation. Final approval names the exact validated
artifact hash and revision. YAML is emitted from typed nodes, never taken from a
model-generated YAML string. `planner_version: 2` selects this implementation when
the workflow engine has an `IWorkflowPlanner` installed; version 1 remains compatible.

Simulation results describe synthetic coverage, not proof of live external behavior.
Required inconclusive scenarios block approval. No model/provider naming heuristics
are used to select runtime behavior.

## Contracts and host integration

```csharp
IWorkflowPlanner planner = new TypedWorkflowPlanner();
IPlanningRuntime runtime = new WorkflowPlanningRuntime(engine);
var next = await planner.AdvanceAsync(snapshot,
    new PlanningCommand { Kind = "advance", ExpectedRevision = snapshot.Revision },
    runtime, cancellationToken);
// Encrypt and persist next with compare-and-swap on snapshot.Revision.
```

`PlanningRequest` requires tenant, session and prompt values. Supply the configured
model under `Options.generator`; the runtime adapter uses the host's `ILLMClient`
and `IMcpClientFactory`. Saving, encryption, reconnect and background lifetime belong
to the host. Commands are `advance`, `answer`, `accept_behavior`, `revise`, `edit_yaml`,
`approve`, `cancel`, `retry` and `edit_intent`. Review commands require the current artifact hash.

Intent assessment cites independent `{sourceId, excerpt}` references from identified
request, answer, existing-workflow and host sources. Each excerpt must occur literally
in its own source. Host constraints and model-written question text cannot establish
user intent. Shape and semantic validation share at most two model calls: the initial
assessment and one targeted repair. Valid outcomes, questions and options are locked
during repair; invalid clarification cannot become `ready` by dropping questions.

Exhausted intent repair pauses in `recovery`, a waiting state with no final failed
outcome. `retry` clears active diagnostics while archiving them. `edit_intent` replaces
the request before the first behavior approval in recovery or early failure, even with
an invalid retained graph, archives prior answers,
and invalidates derived state. Both retain session identity, options, policies, model
usage and cumulative clarification counters. Waiting does not consume active time.
Schema version remains 2; absent counters in older snapshots initialize from retained
answers and the pending form. Reconnection must never submit an answer automatically.

Fragments preserve operation ownership, input/output contracts, reviewed control
flow, execution-time confirmations and finalization. Cache fingerprints include
intent and answers, host constraints, policy, catalog declarations, the fragment,
and referenced workflow boundary schemas. Repairs invalidate affected fragments and
dependent callers. Non-improving candidates are rejected; bounded repair retains the
best candidate. Default concurrency is four.

Behavior assessment uses at most two calls total for shape and semantic validation.
The repair receives exact diagnostic locations, the candidate and the schema index;
unrelated valid nodes, output obligations and finalizers are retained. Exhaustion pauses
in behavior recovery. Retry revalidates retained candidates and always requires their
behavior review before elaboration. `BehaviorAssessmentCalls` survives persistence;
explicit retries start another bounded assessment without resetting cumulative usage.

Reference schemas select exact `capabilityId`/`schemaPointer` pairs from a deterministic
index (for example `/output/properties/message`). Structural fields stay at defaults:
`type: string`, `nullable: false`, null description/items/additionalProperties and empty
properties/enum. Inline schemas instead leave both reference fields null. Combining
them is rejected. JSON Pointer escaping and schema-array traversal are supported;
references that cannot preserve their constraints independently fail explicitly.

`PlanningValue.ResultChannel` is optional: null/`default` retains legacy result addressing,
while `structured` selects validated `mcp.call`/`llm.call` structured-output `.json`.
The compiler validates that channel's declared schema and referenced fields. Original
MCP response fields continue to come from the tool's declared output contract. Merely
listing required field names does not establish a structured-output schema.

Ports have concrete scalar, object and array schemas or JSON-pointer references to
authoritative capability schemas. Technical MCP bindings and step IDs are emitted
deterministically. Unsupported union/constraint conversions, opaque objects, missing
producers and ambiguous imported bindings fail explicitly. Expressions and functions
remain sandboxed Flow code; typed generation alone does not prove correctness.

The bounded synthetic validator probes nominal execution, declared switch/default
and guard outcomes, and integration failure/cancellation with finalization. It forces
branches for structural path coverage and never invokes live external tools. This is
not exhaustive input-space verification. Unreached required paths and inconclusive
checks block final review. Semantic findings require exact request evidence and valid
workflow references; a model score cannot establish correctness.

```sh
dotnet pack src/GnOuGo.Flow.Planning/GnOuGo.Flow.Planning.csproj -c Release
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -o /tmp/planning-smoke
/tmp/planning-smoke/GnOuGo.Flow.Planning.Smoke
```

See [the rollout guide](../../docs/workflow-planning-v2.md),
[evaluation corpus](../../evaluations/workflow-planning/README.md), and
[published smoke exception audit](../../tests/GnOuGo.Flow.Planning.Smoke/README.md).
