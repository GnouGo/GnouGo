# GnOuGo.Flow.Planning

A separately publishable, provider-neutral Planner v2 with one deterministic path:
intent and clarification → locked capabilities → business behavior and human review
→ typed graph and dataflow → unresolved typed fields → targeted typed repairs
→ deterministic YAML → compilation, scenario and semantic validation → final approval.

`PlanningGraph` is the authoritative executable model. The coordinator freezes topology and established contracts, resolves unique bindings,
and asks the model for assignments to remaining field IDs only. One field eligibility
analysis filters types, nullability, availability, obligations and artifact provenance
for both deterministic selection and model schemas. Direct bindings are separate from
computation parameters. Contracts propagate in both directions without rewriting
locked fragments; literals obey destination JSON Schema constraints before dispatch. Callees are validated before callers;
independent workflows run concurrently and commit in stable order. Models never produce
YAML. `PlanningGraphCompiler` owns lowering and capability ownership mappings.

Construction derives prerequisites between active fields on each revision. Ready fields
are ordered by canonical location and packed into bounded requests when independent,
including independent fields on the same node. Schema, default, producer and control-flow
dependencies precede their consumers; variable shared obligations remain sequential.
Contract inclusion proves exclusive unions using discriminator values, finite domains,
types and numeric ranges. Unknown proofs never authorize a binding. Nested contracts and
callee boundaries propagate in both directions, retaining authoritative references.
Schema requests share typed parent contracts and unresolved consumer fields. Result
contracts cannot invent defaults that the runtime does not apply. Before behavior
review, exact capability placement completes uniquely proven activation values;
ambiguous mappings still require a located revision or clarification.
Behavior diagnostics map completed review nodes back to their original staged
coordinates, so inserting a deterministic decision producer cannot redirect a
repair to its neighbor. Missing-implementation patches retain locked operation
ownership and can name only established business inputs. Expression fields state
their executable language in the shared response schema.
Optional declared arguments remain exact holes when business obligations may need
them. Explicit omission retains the member coordinate and is removed only during
lowering; it is distinct from null. Defaults cannot arbitrarily choose which optional
argument carries an outstanding obligation. Finalizers may consume a uniquely owned
materialization behind a coordinator-built existence guard frozen with the skeleton.
Successful public outputs can observe the completed finalizer; failure cleanup never
assumes an earlier materialization succeeded.
When a required artifact contract has no separately declared origin, one available
materializer owned by the accepted workflow can establish its identity. Multiple
materializers, borrowed references, and unavailable explicit origins grant no such
binding. A non-artifact scalar already eligible for direct binding can also be an
operand of an authorized computation, using the same issued identity. Eligibility
reuses contract and provenance results only within its current immutable analysis.

Use `TypedWorkflowPlanner` through Flow.Core's `IWorkflowPlanner.AdvanceAsync` and inject
`IPlanningRuntime`. `WorkflowPlanningRuntime` adapts a Flow engine's model/MCP
clients, telemetry and runtime validators. The integrations package supplies the durable
`WorkflowPlanningRuntimeFactory` for standalone hosts. Every runtime operation carries
explicit ownership or scenario evidence. Hosts own durable, encrypted session storage.

Schema 4 separates intent, construction and validation state. Durable request identities
and receipts support safe recovery. Repairs are atomic, scoped, staged and monotonic;
validation binds the exact graph, contracts and fixtures to the final artifact.
Read-only YAML review follows mandatory business and executable validation.
Progress contracts and shared telemetry report active holes, deterministic resolution,
distinct model exposures, binding/parameter counts and repairs/failures by gate.
Persisted request identities prevent replay from increasing these counts.
Schema resolution is attributed separately to deterministic propagation or the model.
Request accounting separates reservations, receipt-backed usage and unverifiable attempts;
unknown token usage remains null. Workflow/phase/gate totals include estimated and actual
tokens, and executable requests record their semantic reasons and avoidable-call audits.
Required-decision attribution remains available after model resolution, separately
from actual receipt evidence and repeated exposures.

Defaults: concurrency 4, repairs per workflow/gate 5, input ceiling 12,000 tokens per request,
output ceiling 8,192 tokens. Limit exhaustion pauses with diagnostics.

Capability selection sizes complete requests, including response schemas, against
the same input ceiling. Each catalog page permits only its declared IDs. Matching
and matching repair use recorded candidates for the affected operation plus declared
prerequisites; completed scopes are checkpointed and revalidated on replay. Unresolved
contracts stop without reopening the unfiltered catalog.
Matching context shares repeated catalog text and emits ordinary UTF-8 JSON without
HTML escaping. Candidate IDs and expanded contract text are preserved exactly.
Required workflow policies remain in selection and matching context even when they
do not constitute global catalog denials. Workflow structure evidence is distinguished
from intrinsic capability requirements, so implementation restrictions remain visible.

Semantic review uses established graph contracts for local values. Generic native
executor schemas do not override business outputs or grant capability-revision
targets. Reassessment of an invalid review target preserves graph, behavior,
contract and scenario fingerprints, along with consumed repair allowances.

```sh
dotnet build src/GnOuGo.Flow.Planning
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke
```

See [the architecture](../../docs/workflow-planning-v2.md) and
[published smoke](../../tests/GnOuGo.Flow.Planning.Smoke/README.md).
