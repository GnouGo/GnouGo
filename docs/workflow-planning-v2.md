# Workflow planning

`Prompt → compact business intent → capability resolution → deterministic graph → validation → bounded corrections → compile → scenarios → approval`

The LLM understands the business request. The engine builds and authorizes executable workflows. There is one planning path and no compatibility mode.

The architecture is frozen. The accepted behavior revision is `65dc34a`: the [live reliability evaluation](planning-default-response-domains-2026-09-20.md) passed the seven-case pilot and all measured gates, with 20/21 FinalReview, 18/21 within two physical calls, median one call, zero safety violations and 85/85 independent execution variants passing. This is evidence from one pinned model with mocked integrations, not a guarantee for arbitrary requests or real external execution. Later cleanup and documentation commits do not constitute another live evaluation.

The [real Server follow-up](planning-server-live-2026-09-21.md) records four PR-review planning attempts and small deterministic inference/retrieval corrections through `f18d890`. None reached executable approval; the last stopped on an uncertain provider HTTP 500. This evidence remains separate from the accepted benchmark cohort.

## Three core files

Start in `src/GnOuGo.Flow.Planning`:

- `TypedWorkflowPlanner.cs` advances the durable session, dispatches bounded requests and manages validation and approval.
- `PlanningGraphBuilder.cs` resolves operations, infers contracts, establishes scopes and dependencies, and lowers business control flow.
- `PlanningGraphCompiler.cs` accepts a validated, hole-free graph and emits deterministic YAML and capability bindings.

Core owns provider-neutral contracts and runtime execution and references no other GnOuGo project. Planning depends only on Core and publishes independently. Integrations and hosts inject models, transports, policy and persistence.

## What the model describes

`WorkflowIntentPlan` contains a summary, inputs, operations, outputs, optional named subflows and business clarification questions. The entrypoint is implicit. Operation variants contain only their relevant fields:

| Operation | Business decision |
| --- | --- |
| `invoke` | Capability, arguments, optional business fallback |
| `calculate` | Sandboxed calculation over named values |
| `transform` | Model instruction and business data |
| `choose` | Condition and two result-producing blocks |
| `each` | Collection, iteration body and parallel preference |
| `parallel` | Named independent blocks |
| `call` | Named subflow and its arguments |
| `cleanup` | Operations to run on exit |

Operations have logical identifiers, optional dependencies and business conditions. References address an input, operation result, iteration value or index and a business field path. They never name runtime variable paths or result envelopes. Calculations use named business arguments; workflow-level helper functions and arbitrary executor settings are unavailable.

Known input and output contracts come from capability schemas, native contracts and connected values. Static expression inference supports common literals, objects, arrays, arithmetic, comparisons, conditionals, member access and array mapping. An optional small type declaration describes only a new business value whose type cannot be derived. Unknown computations or conflicting types remain diagnostics; executing sample expressions never establishes a contract.

The builder creates MCP request/result wrappers, structured model results, helper subflows and captures, fixed arguments, result projections, fallback envelopes and cleanup availability guards. Internal names occupy a reserved namespace. Omitted arguments, a `missing` value, explicit null and declared defaults remain distinct. Default execution stops on failure without retry. Intent cannot configure technical retries.

New interpretation and correction schemas restrict input defaults to recursive literals, including subflow declarations. JSON `default: null` means absence; `default: {"kind":"null"}` is an explicit value and is excluded for declared non-nullable types. Inferred contracts and complete literal compatibility remain deterministic validation responsibilities. The engine never silently rewrites a default.

Cleanup runs after main execution, including failure and cancellation. Its `after` dependencies express ordering, not successful completion or output availability. The builder derives availability guards from actual resource references; explicit conditions and permissions still apply. Cleanup cannot consume a resource whose acquisition failed or was skipped.

Native workflow ports can retain an authoritative JSON `schema` when shorthand types cannot express its constraints. Validation, scenario sampling and runtime input/output checks preserve that schema, including defaults, patterns and numeric bounds. This is an engine-owned lowering detail, not a schema-copy task for the model.

## Discovery and bounded decisions

Discovery validates the full allowed catalog without a model call. Compact cards expose issued IDs, descriptions, editable argument signatures, business result fields and effects. Fixed arguments, transport details and full schema documents remain authoritative host data.

Deterministic text retrieval ranks names, descriptions and producer metadata with stable ID tie-breaking. Initial context contains at most 24 cards and uses at most half the configured input allowance. The full allowed catalog remains available when an operation is unresolved. Retrieval affects exposure only; it cannot authorize a capability or prove a business match.

Finite holes use the same rule throughout: zero valid choices produce a located diagnostic; one resolves without a call; multiple choices require a bounded selection from issued IDs. Independent choices are batched, selections are validated, and dependent domains are recomputed. A domain that cannot fit the request budget fails explicitly instead of silently losing alternatives. Business assignments update intent before rebuilding; technical assignments are reproducible builder decisions. No candidate domains are persisted.

## Local correction and scenarios

Corrections replace engine-issued argument values, declarations, operations, operation groups or literal fixture targets. Requests contain exact diagnostics, affected fragments, bindings and relevant authoritative contracts. Unknown targets, duplicate or overlapping edits and host-field changes are rejected. Application is atomic, followed by a complete rebuild and validation. Whole-intent generation is used when no valid intent was parsed or the user revises the request. Unchanged candidates with unchanged failures stop early.

`PlanningCorrections.cs` selects at most three independent targets per new typed repair. Invalid upstream inputs and producers precede consumers; intent order breaks ties. A calculation's value precedes its result declaration. Necessary topology groups remain indivisible and run alone. Selection is recomputed after complete validation, with no persisted queue. Deferred diagnostics remain blocking and the request reports their target count. Invalid edits preserve the original intent and its diagnostics.

Context is limited to selected fragments, original user/host instructions, relevant bindings, conditional scopes and authoritative editable contracts. Neighboring contracts retain referenced value constraints and local schema references; contracts that cannot safely be projected keep their root and explicit field path. `PlanningCapabilityCards.cs` supplies up to four advisory alternatives per unresolved invocation, ranked from the full allowed catalog by purpose and bindings. Invalid arguments do not filter this advisory retrieval. This never replaces permission checks or the complete zero/one/multiple-choice domain.

The complete typed repair request, including its response schema, must fit 12,000 estimated input tokens and an 8,192 output-token ceiling, or smaller configured limits. The engine reduces the batch to fit; a single oversized target stops with a located explanation before spending a call or repair. Each dispatched batch consumes one repair attempt. These limits do not change initial interpretation or whole-intent regeneration settings. Pending requests, including historical larger repairs, replay with their original identities, targets, schemas and ceilings.

See the [focused-repair validation report](planning-repair-batches-2026-09-20.md) for offline request measurements and current limits. It is not another live-model evaluation.

Session diagnostics retain graph coordinates. Repair locations are recomputed by logical identifiers, scopes and member names, including reordered steps, confirmation wrappers, nested control flow and cleanup. Generated block input schema errors follow their caller bindings back to the business input, operation result or iteration source, including nested scopes. A valid source with a defective generated contract remains a host failure. Compiler errors preserve nested value locations through MCP wrapping; catalog-fixed arguments remain host-owned. Missing arguments point to their existing container. Producer failures group their dependent binding consequences in repair context; independent and blocking findings remain visible. Generated host defects stop without spending a model repair.

Scenarios sample schemas, defaults and validated producer examples first. Literal input/observation fixtures belong to the session, not initial intent. They are requested through the same repair budget only when deterministic sampling cannot satisfy a contract. Fixture failures are validated alongside intent failures. Runtime input declarations do not require planning-time answers. Isolated scenarios exercise normal execution, control flow, failures, cancellation, cleanup and rejected confirmation. They are validation evidence, not evidence of live external success.

## Safety and public boundaries

Deterministic checks cover capability existence, schemas and literals, bindings, conditional availability, dependencies/cycles, executable expressions, output contracts, artifact relationships and host policy. Names and descriptions never grant permissions. Unknown external effects require conservative confirmation. A host-generated gate encloses protected operations, including reachable subflows and finalizers; denial prevents entry. Final workflow approval remains separate from runtime confirmation.

Producer metadata may declare `gnougo.result.detect_errors` as a boolean when a valid business result contains failure states. Discovery locks the corresponding executor error policy. This controls content-envelope heuristics only: an MCP `IsError` transport result still fails. The model cannot set this policy.

`IWorkflowPlanner.AdvanceAsync(session, command, runtime, cancellation)` advances one durable checkpoint. Commands include `advance`, `answer`, `revise`, `edit_intent`, `configure_generation`, `approve` and `cancel`. Agent.Server also owns saving and explicit operator retry. Optimistic revisions protect every update; approval requires the exact reviewed artifact hash. Changes invalidate approval.

`IPlanningRuntime` supplies discovery, model calls, executable/scenario validation, current-contract and host-policy verification, and checkpoints. Saving and `workflow.execute` retrieve trusted approved artifacts by tenant, session and hash and revalidate current contracts/policy. Arbitrary prior-step YAML cannot substitute for approval. Graph import projects supported saved workflows into compact revision context; unsupported executor constructs are rejected explicitly.

Domain publication rules remain in injected host integrations. Agent.Server's [review boundary](../src/GnOuGo.Agent.Server/Reviews/README.md) captures original observations and owns review evaluation, separate confirmation, fresh head verification and durable publication. The planner has no PR-specific path.

## Inspecting planning traces

Agent.Server's `/planning` page lists Designer and Chat sessions with a **Traces**
action. Chat sessions open at `/planning/{id}?source=workflow` as read-only details;
their originating workflow continues to own approvals and execution. The shared
trace panel reads exact session/tenant matches from local capture and retained
collector storage. Separate execution traces stay selectable by timestamp rather
than being combined. Inspection never resumes planning or spends model allowance.
See the [trace UI validation report](planning-traces-validation-2026-09-20.md) for
restart, isolation, browser and build checks. Missing or expired traces are shown
explicitly; encrypted request receipts remain independently retained.

## Storage, budgets and restart

Schema 7 uses fresh encrypted `*-v7` collections and Agent.Server's `.GnOuGo/data/gnougo-planning-v7.db`. Earlier formats are rejected with no adapter or migration; existing databases remain untouched. EF Core/SQLite indexes and compiled models remain in the host. Public KeyVault record APIs encrypt tenant-scoped payloads and receipts.

One session owns intent, graph, diagnostics, scenarios/fixtures, approval, cumulative accounting and one pending model request. There are no semantic assessments, proof chains, persisted diagnostic maps or separate resolution ledgers.

Defaults are eight calls, two repair attempts per submitted intent and medium reasoning. Standard request limits remain 12,000 input and 8,192 output tokens; hosts retain configured total-token, cost and active-time limits. The evaluation runner explicitly uses 96,000/32,768. User revisions reset only the repair allowance; restart replenishes nothing.

Requests are reserved durably before dispatch. Completed receipts replay under their original response schemas without another charge. Uncertain dispatches stop without automatic resend. Agent.Server's explicit operator retry retains prior charges and conservatively accounts for unknown usage before reserving a new request; ordinary restart never invokes it. Saving reconciles an already committed identical artifact.

Transport retries belong to AI.Core's shared HTTP layer, not the planner. It handles only transient failures within configured attempt/time limits, honoring `Retry-After` and caller cancellation. The benchmark explicitly supplies a durable HTTP journal for synchronous, side-effect-free generation: it may reserve conservative possible usage and retry one uncertain attempt under a new identity. This opt-in recovery does not apply to workflow effects or review publication, and it never replenishes session or campaign budgets. See the [retry contracts](../src/GnOuGo.AI.Core/README.md) and [benchmark recovery documentation](../tests/GnOuGo.Agent.Planning.Benchmark/README.md).

## Validation

```bash
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet build GnOuGo.Agent.sln -c Release -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -m:1 -warnaserror
dotnet build tests/GnOuGo.Agent.Planning.Benchmark -m:1 -warnaserror
dotnet run --no-build --project tests/GnOuGo.Agent.Planning.Benchmark
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 -m:1 -warnaserror
```

See the [benchmark runner](../tests/GnOuGo.Agent.Planning.Benchmark/README.md), [Native AOT smoke](../tests/GnOuGo.Flow.Planning.Smoke/README.md), [accepted reliability report](planning-default-response-domains-2026-09-20.md), and [merge finalization and release checks](planning-finalization-2026-09-20.md). The [business-intent migration report](planning-business-intent-validation-2026-09-19.md) and [earlier schema-6 live report](planning-live-validation-2026-09-18.md) retain historical evidence; they do not describe the final validation state.
