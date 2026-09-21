# GnOuGo.Flow.Planning

A separately publishable planner depending only on Flow.Core.

`Prompt → business intent → deterministic graph → validation → bounded correction → scenarios → approval`

The architecture is frozen, with accepted behavior at `65dc34a`. The [reliability report](../../docs/planning-default-response-domains-2026-09-20.md) records a clean 7/7 pilot and 20/21 measured FinalReview, with zero safety violations. First-pass validity was 12/21; the integrations were mocked. See the [architecture](../../docs/workflow-planning-v2.md) for public boundaries and limitations.

Start with `TypedWorkflowPlanner.cs`, `PlanningGraphBuilder.cs`, and `PlanningGraphCompiler.cs`. The model describes business operations. The builder owns executable nodes, capability bindings, schemas, result envelopes, internal subflows, ordering, and finalizer guards. The compiler only accepts valid, fully resolved graphs.

Intent contains inputs, operations, outputs, optional named subflows and clarification questions. Its operation variants are `invoke`, `calculate`, `transform`, `choose`, `each`, `parallel`, `call`, and `cleanup`. Values address inputs, operation results and iteration values through business paths. Calculations use sandboxed expressions over named values. Authoritative JSON Schema constraints and defaults survive native port lowering, including inputs consumed only inside nested business blocks. Only new business values need type declarations; capability contracts remain catalog-owned. Omitted arguments, missing values, explicit null and defaults remain distinct. Fixtures belong to the session, not the interpretation response.

Capability discovery is model-free. Compact cards are ranked by deterministic text retrieval over names, descriptions and producer metadata, with document-length normalization to reduce incidental matches from verbose descriptions. At most 24 cards use at most half the input allowance. The full authorized catalog remains available for unresolved operations; retrieval never grants permission or establishes semantic correctness. Candidate domains that cannot fit a bounded decision fail with an input-budget diagnostic.

Holes use zero/one/multiple-choice resolution. Zero choices produce a located diagnostic, a singleton resolves automatically, and multiple choices require issued IDs. Resolved business assignments update the intent before rebuilding; technical assignments are builder-owned. There is no persisted candidate-domain state.

Input defaults, including nested members and array items, accept only literal candidates from declared contracts. Runtime inputs and operation results never supply defaults. An unresolved default with no finite choices receives an input-declaration repair; a declared runtime input needs no planning-time answer. `default: null` means no default, while `default: {"kind":"null"}` is an explicit null value. Invalid defaults are diagnosed before scenario sampling. Port diagnostics match declarations and object members by name and retain exact value locations. Choice batches commit their graph and intent changes only after reflection and rebuilding both succeed; host reflection failures stop without consuming a model repair.

New interpretation and correction requests constrain defaults to recursive literals, using the same definitions as fixture corrections. Explicit null is excluded for non-nullable declarations. Inferred contracts still determine nullability, member types, array items and enum constraints during graph validation. Receipt replay retains the originally reserved response schema; tightening a new request never rewrites an earlier response or its accounting.

Generated block input diagnostics follow their captured value back to the business input, producer or iteration source. Nested compiler failures retain the exact value path through arrays, objects and MCP wrapping. These locations are recomputed; no mapping state is persisted. A defective generated input with a valid source contract, a catalog-fixed argument or a confirmation guard remains a host failure and stops without a model repair.

Computation validation, contract inference and compilation use the same expression normalization. Supported function bodies must end in an explicit `return`; an unknown inferred type still requires an established contract. Unexpected host exceptions stop the session immediately, preserving receipts and diagnostics instead of repeatedly advancing or spending model repairs on an engine defect.

An array of authored string literals derives a string-item enum containing every distinct element. It is not constrained to the first element's singleton enum. These are executable literals, not scenario samples; narrower capability contracts still reject out-of-domain elements.

When an unresolved invocation contains a tool name instead of an issued capability ID, repair advice prioritizes exact name matches already present in the allowed catalog. This is retrieval evidence only: names remain invalid executable references, and a correction must select an issued ID and satisfy its full contract. The advisory list remains bounded to four cards; deterministic choice domains are unchanged.

Repairs replace engine-issued argument values, declarations, operations or operation groups atomically. Requests include affected fragments, bindings and exact diagnostics. Unknown targets, overlapping/duplicate edits, host-field edits and nonliteral fixtures are rejected. Whole-intent regeneration is reserved for an unparsed response or an explicit user revision. Dependency repairs include business scopes, main/finalizer placement and structurally eligible predecessor IDs. Those hints are recomputed from the graph; they do not grant permissions or replace complete validation. Shared calculation and cleanup examples clarify parameter bindings and ordering. Unchanged repairs stop early. Scenario fixtures are requested through this same repair budget only when deterministic sampling cannot satisfy an authoritative contract.

New typed repairs select at most three non-overlapping targets, correcting erroneous inputs and producers before dependent consumers, with stable intent order for ties. Topology groups run alone. Each dispatched batch consumes one repair attempt; a full rebuild and validation determines the next batch. All outstanding diagnostics remain blocking, including deferred errors and errors retained after rejected edits. No batch queue is persisted.

Repair context contains the original request, host instructions, selected fragments, exact diagnostics, relevant bindings/scopes and authoritative editable contracts. Neighboring result contracts are projected to referenced fields when constraints can be preserved; otherwise the root contract and field path remain explicit. Unresolved invocations receive at most four advisory cards ranked by their logical identifier, purpose and bindings, independently of malformed arguments. This searches the full allowed catalog without changing the initial shortlist or deterministic eligibility. Actual hole choice domains remain complete. Response schemas expose only issued target IDs and required definitions.

When a producer has no declared result contract, repair bindings mark it as `absent`; a known contract with an invalid field remains `declared`. If a value-only correction cannot establish a typed boundary, the engine issues the smallest containing business block (its operations and result together) as one atomic topology target. The existing block schema is reused, sibling blocks are excluded, and topology targets run alone. Ordinary argument values keep their local targets and receiving contracts; they do not expand to a workflow-wide topology edit. A correction may retain the invocation and add a local `calculate` with a derived `resultType` and explicit validation before exporting the result. The tool's schema stays unknown. JSON text is never assumed, and malformed data must fail rather than be converted into successful evidence. Runtime output validation and protected-action approval remain in force. These targets and source relationships are recomputed; original reserved repairs retain their original shapes on replay.

Typed repair requests are bounded to 12,000 estimated input tokens **including the response schema**, and 8,192 output tokens, or the configured limits if lower. Batches shrink until they fit. An indivisible target that cannot fit produces `MODEL_INPUT_LIMIT` with its identity, location and required size before any reservation or charge. Initial interpretation settings are unchanged. Global defaults remain eight calls and two repairs; the dedicated E2E agent may explicitly allow six repairs within the same eight-call ceiling. Already-reserved requests retain their original targets, schemas and output ceilings on restart, including older larger requests.

The [repair-batch validation report](../../docs/planning-repair-batches-2026-09-20.md) records retained-evidence request-size measurements, offline checks and the unresolved provider gate. Those measurements did not dispatch a model or resume a stopped session.

The [Designer recovery report](../../docs/planning-designer-recovery-2026-09-21.md) records capability-advice and untyped-boundary regressions, a failed single live planning attempt, and the subsequent offline correction to argument targeting. Request-size improvements do not establish a successful live repair.

The [follow-up Designer test on a3f56bc](../../docs/planning-designer-live-a3f56bc-2026-09-21.md) stopped before repair: the provider exhausted the 8,192-token output allowance in reasoning and returned no intent. Its completed receipt and charges are preserved; no further attempt or limit increase was made.

The separately authorized [low-reasoning Designer test](../../docs/planning-designer-low-2026-09-21.md) returned intent and dispatched two focused repairs within the unchanged limits. It still stopped with capability recovery and invalid-reference errors; its cleanup choice was also semantically unsuitable. This is not a successful end-to-end workflow result, and global reasoning defaults remain unchanged.

The [real Server validation report](../../docs/planning-server-live-2026-09-21.md) records four subsequent chat attempts, three reproduced fixes and the final uncertain HTTP 500 dispatch. No real PR review reached execution; the accepted mocked reliability results do not establish this product path's success.

Cleanup runs after main execution, including failure and cancellation. Its `after` edges express ordering only; they do not require predecessor success or establish result availability. The builder guards actual resource references, while explicit `when` conditions still apply. The compiler short-circuits availability before evaluating a condition that needs the resource. A failed or skipped acquisition therefore cannot supply a resource to cleanup. Stored executable graphs and approvals are not rebuilt implicitly; revised intents require fresh validation and approval. An artifact whose stored YAML no longer matches current compilation must be revised and reviewed again.

The graph remains executable authority. Validation checks capability identity, declared schemas, bindings, conditional availability, dependencies, expressions, artifact contracts and host policy. Protected effects require a host-generated runtime confirmation gate, including subflows and cleanup. Human approval of the final artifact is a separate gate. Saving and execution require trusted stored approval and current contracts.

Hosts inject `IPlanningRuntime`. Schema-7 sessions retain cumulative budgets, one durable pending request, completion receipts, scenarios and approval. Earlier formats are rejected. Agent.Server uses new encrypted record namespaces and a new planning database; existing databases are untouched. Reserved requests replay under their original response schemas. Uncertain dispatches stop without automatic resend.

Provider transport retries are owned by the shared HTTP layer. The benchmark can opt into durable, conservatively accounted recovery for one uncertain side-effect-free model attempt under a new identity. This is separate from planner repair and does not authorize retrying workflow effects or publication.

Revision import projects supported saved workflows into business intent and rejects unrepresentable executor configuration explicitly. No raw graph escape hatch is exposed to the model.

```bash
dotnet build src/GnOuGo.Flow.Planning -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests
dotnet pack src/GnOuGo.Flow.Planning -c Release
dotnet run --project tests/GnOuGo.Agent.Planning.Benchmark
```
