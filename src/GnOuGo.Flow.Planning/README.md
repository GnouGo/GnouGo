# GnOuGo.Flow.Planning

A separately publishable planner depending only on Flow.Core.

`Prompt → business intent → deterministic graph → validation → bounded correction → scenarios → approval`

Start with `TypedWorkflowPlanner.cs`, `PlanningGraphBuilder.cs`, and `PlanningGraphCompiler.cs`. The model describes business operations. The builder owns executable nodes, capability bindings, schemas, result envelopes, internal subflows, ordering, and finalizer guards. The compiler only accepts valid, fully resolved graphs.

Intent contains inputs, operations, outputs, optional named subflows and clarification questions. Its operation variants are `invoke`, `calculate`, `transform`, `choose`, `each`, `parallel`, `call`, and `cleanup`. Values address inputs, operation results and iteration values through business paths. Calculations use sandboxed expressions over named values. Authoritative JSON Schema constraints and defaults survive native port lowering, including inputs consumed only inside nested business blocks. Only new business values need type declarations; capability contracts remain catalog-owned. Omitted arguments, missing values, explicit null and defaults remain distinct. Fixtures belong to the session, not the interpretation response.

Capability discovery is model-free. Compact cards are ranked by deterministic text retrieval over names, descriptions and producer metadata. At most 24 cards use at most half the input allowance. The full authorized catalog remains available for unresolved operations; retrieval never grants permission or establishes semantic correctness. Candidate domains that cannot fit a bounded decision fail with an input-budget diagnostic.

Holes use zero/one/multiple-choice resolution. Zero choices produce a located diagnostic, a singleton resolves automatically, and multiple choices require issued IDs. Resolved business assignments update the intent before rebuilding; technical assignments are builder-owned. There is no persisted candidate-domain state.

Input defaults, including nested members and array items, accept only literal candidates from declared contracts. Runtime inputs and operation results never supply defaults. An unresolved default with no finite choices receives an input-declaration repair; a declared runtime input needs no planning-time answer. `default: null` means no default, while `default: {"kind":"null"}` is an explicit null value. Invalid defaults are diagnosed before scenario sampling. Port diagnostics match declarations and object members by name and retain exact value locations. Choice batches commit their graph and intent changes only after reflection and rebuilding both succeed; host reflection failures stop without consuming a model repair.

Repairs replace engine-issued argument values, declarations, operations or operation groups atomically. Requests include affected fragments, bindings and exact diagnostics. Unknown targets, overlapping/duplicate edits, host-field edits and nonliteral fixtures are rejected. Whole-intent regeneration is reserved for an unparsed response or an explicit user revision. Dependency repairs include business scopes, main/finalizer placement and structurally eligible predecessor IDs. Those hints are recomputed from the graph; they do not grant permissions or replace complete validation. Shared calculation and cleanup examples clarify parameter bindings and ordering. Unchanged repairs stop early. Scenario fixtures are requested through this same repair budget only when deterministic sampling cannot satisfy an authoritative contract.

Cleanup runs after main execution, including failure and cancellation. Its `after` edges express ordering only; they do not require predecessor success or establish result availability. The builder guards actual resource references, while explicit `when` conditions still apply. The compiler short-circuits availability before evaluating a condition that needs the resource. A failed or skipped acquisition therefore cannot supply a resource to cleanup. Stored executable graphs and approvals are not rebuilt implicitly; revised intents require fresh validation and approval. An artifact whose stored YAML no longer matches current compilation must be revised and reviewed again.

The graph remains executable authority. Validation checks capability identity, declared schemas, bindings, conditional availability, dependencies, expressions, artifact contracts and host policy. Protected effects require a host-generated runtime confirmation gate, including subflows and cleanup. Human approval of the final artifact is a separate gate. Saving and execution require trusted stored approval and current contracts.

Hosts inject `IPlanningRuntime`. Schema-7 sessions retain cumulative budgets, one durable pending request, completion receipts, scenarios and approval. Earlier formats are rejected. Agent.Server uses new encrypted record namespaces and a new planning database; existing databases are untouched. Reserved requests replay under their original response schemas. Uncertain dispatches stop without automatic resend.

Revision import projects supported saved workflows into business intent and rejects unrepresentable executor configuration explicitly. No raw graph escape hatch is exposed to the model.

```bash
dotnet build src/GnOuGo.Flow.Planning -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
```
