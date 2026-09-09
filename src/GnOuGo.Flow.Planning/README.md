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
Known computed result fields must satisfy schema-valued `additionalProperties`, including
when the contract declares no named properties. A nominal scenario that uses an error
fallback remains inconclusive with `SCENARIO_RECOVERED_ERROR`; successful continuation
does not demonstrate the normal path. Explicit failure and cancellation scenarios retain
their separate recovery and cleanup checks.
Loop observation fixtures are generated one observation per bounded call. Their locked
sequence length and completed values survive restart; an incomplete sequence cannot pass
scenario validation. Active fixture phases are checkpointed before dispatch. An output-token
ceiling pauses in recovery with `MODEL_OUTPUT_LIMIT`, preserving the workflow and avoiding
an identical automatic retry. Synthetic observations still require actual deterministic
execution to consume the complete sequence and terminate; they do not prove live behavior.
Semantic assessment repairs select exact source excerpts or workflow-owned locations for
diagnosed fields only. Supported findings cannot be removed by that repair. Source roles
remain distinct, and model-written questions or generated contract prose cannot establish
user intent. Missing external observations return to capability preparation and behavior
review; a repaired citation alone does not establish executable correctness.
Behavior revision requests share repeated schema vocabularies and include capability
contracts only for the affected subtrees, with incoming operations described as boundaries.
The complete revised behavior still undergoes validation against the full locked preparation
and requires a new approval. Context failures report estimated size and the configured limit.

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
`approve`, `cancel`, `retry`, `edit_intent` and `configure_generation`. Review commands require the current artifact hash.

Early-reviewed executable construction is partitioned into units of at most four nodes,
without changing workflow boundaries. Input and producer contracts precede implementations;
workflow-call implementations wait for the callee's public outputs. Independent ready units
run with the configured session concurrency (four by default). Each response has exact node
keys and native-specific fields. Switch values, order and defaults come from approved
behavior; there are no model-generated case indices. Runtime confirmations are constructed
through `HumanInputContract`, including scalar choices and their boolean response contract.
Computed tool arguments with explicit null result branches are rejected before execution
when their destination excludes null. Optional arguments may be omitted; optionality does
not make null a valid value. Unknown computations still require runtime scenario validation.
When a scenario leaves observations unconsumed inside a loop, repair targets the containing
loop's iteration controls. The read operation's arguments remain outside that patch.
In a sequential loop, an items array bounds iteration even when a while condition is true;
coverage must establish complete traversal and termination without fixing the count to examples.
Semantic review preserves unresolved findings when the affected input, its producers, helpers
and control path are unchanged. Renamed finding codes and newly discovered defects in unchanged
code do not discard an unrelated repair. New findings on changed code remain potential
regressions. Retry restores an exact comparison baseline only under the current construction
contracts; an empty later review cannot erase an unchanged required finding.
Input/output reference sources are constrained to declared keys. Public exports select from
a deterministic index of resolvable, exportable producer paths; schemas come from the selected
producer. Optional fields cannot be selected as unconditional required values. Whole-object
exports retain optional properties. Missing contracts require a validated transformation.
Parallel outputs retain their fixed branch positions and declared child envelopes. Exact
bindings expose raw and structured fields separately; provenance follows only original
producer values, and a fallback without the field prevents unconditional selection.
Construction and repair receive the exact containing result contract for required operations
inside branches or loops. Consuming that result preserves uncertainty; it does not prove the
operation ran or succeeded, and it cannot substitute for an explicit permission binding.
For a reviewed linear sequence whose tools all implement the same operation, operation-level
inputs must reach the final result. That result must also consume each intermediate producer.
An all-read sequence instead retains each tool's locked input dependencies and collects
their outputs through the native sequence result; its last tool need not accept unrelated
observations as arguments. Every member must explicitly declare a read effect. Unknown,
computational, conditional, or mutating compositions retain the stricter dependency checks.
Conditional or differently owned steps retain their individual requirements. Confirmation,
artifact identity, and capability validation continue to apply to every constituent action.
Multiple effects may share a gate only when their declared decision source, response contract,
outcomes and required permissions agree. Distinct sources require separate gates or an explicit
reducer; they fail before model dispatch instead of selecting an arbitrary permission.
The compiler supplies capability ownership bindings alongside YAML for final validation and
approval. Calls with identical transport contracts retain distinct operation owners. The
runtime verifies each binding against the actual step, locked request fields, and capability
identity; an unbound call or a substitute confirmation fails validation. These bindings are
derived from the retained graph and add no fields to executable YAML.

Construction contract 29 uses separate schema response variants for strings, primitive
scalars, arrays, and objects. Models return only the fields relevant to each type. Legacy
empty annotations are normalized without removing substantive constraints, and retained
candidates are revalidated. A provider output-limit result is recorded as `MODEL_OUTPUT_LIMIT`
before JSON conversion and cannot overwrite retained fields. Unstarted multi-node units can
split; a single exhausted unit pauses with its receipts and configured ceiling intact.
Contract 30 also relocates legacy array field declarations into an existing inline object
item when duplicate declarations agree exactly. Every field and constraint is retained;
conflicts require repair. Schema repairs receive schema coordinates and specific nested
validation findings, without unrelated runtime bindings or computation instructions.
Contract 31 distinguishes stopping handlers from continuing fallbacks. Only continuation
must produce the declared structured result; stopping cannot publish a successful result
or enable later steps. Dependency and expression checks still apply to both handlers.
Contract 32 projects complete or partial container results to the logical child names
declared by their planning schemas. Nested sequences, branches, iterations and previous
iteration values retain absent/null outcomes and raw versus structured envelopes. Exact
leaf bindings retain direct runtime addresses; tool-owned field names are never rewritten.

`PlanningRequest.Generation` defaults to 12,000 estimated input tokens per unit and an
enforced 8,192 output-token ceiling per call. Oversized groups split before dispatch; an
oversized single contract pauses with `UNIT_CONTEXT_TOO_LARGE`. Optional structured fields
become required nullable properties; authoritative references are never silently rewritten.
Candidates are validated before conversion, and invalid fields receive atomic scoped patches.
Shape failures consume the configured repair allowance. Encrypted checkpoints retain candidate
hashes, request hashes, dependencies, validation findings and cumulative repair counts.
Dispatch findings are separate, so context/transport failures cannot hide candidate defects.
Repeated binding paths use a shared prefix only when that representation is shorter. Every
identifier, path, type and availability remains recoverable without changing the token ceiling.
An oversized single-node implementation is generated in bounded field groups. Valid field
patches are checkpointed as partial candidates and resumed without regenerating completed
coordinates. Missing fields are unfinished generation; malformed responses consume the normal
repair allowance. Approval still requires full conversion, contract, compilation and scenario checks.
Container input reachability is checked after its child implementations are available;
the complete workflow must still satisfy every accepted business-input obligation before review.
Retry reuses validated units; changed intent, catalog, executor contracts or dependencies
invalidate affected checkpoints. Waiting does not consume active planning time.

`configure_generation` takes `generation` options and an exact revision while paused. It
retains accepted behavior and answers, records prior settings, and invalidates pending work
and final approval. Hosts may override reasoning through `Generation.Reasoning`; null preserves
the caller's generator setting. Journaled calls disable transport retries and fail closed if
compatibility would remove their output ceiling. Legacy approved graph sessions retain the
existing fragment path and YAML compatibility.

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

Behavior review uses `PlanningBehaviorPlan`, a business contract with operation owners,
ports, named decision outcomes (including explicit defaults), confirmations, calls and
cleanup. It contains no executable schemas, expressions or functions. At most two calls
are used to validate this plan against the capability contract before review.
Behavior response variants constrain capability IDs to the selected catalog bindings,
operation IDs to the declared inventory, and each native executor to its supported
behavior kind. Producing a native decision and routing its outcomes are separate nodes.
`ApprovedBehaviorHash` locks the exact accepted contract. Elaboration preserves its
stable keys and obligations; changing actions or control flow requires another review.
Unapproved legacy candidates return through this review; previously approved graphs
retain their approval semantics. These additive fields keep snapshot schema version 2.

Elaboration owns executable fields for each accepted node. Workflow/node
identity, capabilities, ownership and branch/finalizer placement are copied deterministically
from the accepted behavior, never regenerated by the model. Missing or duplicate node
fields fail validation. Missing ownership is derived from an exact selected capability
only when there is one unambiguous workflow owner; conflicts remain diagnostics.
Fragment response variants constrain fields by accepted native step type: unsupported
structured post-processing, non-`set` output annotations, ignored `set.expr`, and loop
variables outside loops must be null before elaboration. Empty fragments remain valid.
Local-processing capability obligations may use validated native calculations or control
flow; external and native bindings still require their exact declared executor. Older
capabilities without resolution metadata retain strict binding checks. Logical child
references inside sequence and switch results are lowered to their declared runtime IDs
without renaming payload fields, quoted text or locally shadowed variables.

Executable responses use compact discriminated schemas (`inline` or `reference`) and
values (literal, input, output, workflow, expression or template). References select
exact capability/schema-pointer pairs from a deterministic index. Mixed declarations
are rejected. A node's `StructuredOutput` is the single declaration lowered into
runtime `structured_output`; original results and validated `.json` remain distinct.

Independent validation collects schema, producer, native-input, template, expression
and function-syntax findings before repair. Only `set` emits `output_schema`; other
annotations must describe actual producer results. `set` computes its output in input
values (including an object-producing whole-input expression), and ignored `expr`
fields are rejected. Computed results must satisfy their runtime assertion. Compiler and runtime interpolation
share JavaScript token boundaries, including nested braces, comments and strings.
Template bindings may compose objects and arrays containing typed references; lowering builds
executable expressions for those values instead of treating nested references as literals.
Confirmation choices use `HumanInputContract`. Logical-expression type inference follows
JavaScript operand-return semantics. Runtime contract, capability provenance, scenario,
and semantic-review passes retain separate validation progress.

Capability metadata includes validated artifact producers and consumers. A loop can
route an unchanged artifact through an explicit item source and its child result envelope;
opaque helper calls and transformed artifact values remain unproven. Generation receives
the deterministic runtime result-key index for container and loop-item addressing.

Executable repairs are atomic patches addressed by workflow/node key and permitted
field path. Unaffected fields, accepted actions, branch outcomes and finalizers stay
fixed. A schema may be added to an unchanged literal set only after proving the value
satisfies it. Removing a non-executable annotation on another step preserves its
authoritative producer contract. Rejected patches consume the configured repair
budget and retain their findings separately; they never broaden the allowed scope.
The next repair also receives rejected validation findings, allowed paths and repair
hints. A diagnosed loop consumer can repair its owning item source; a diagnosed
computation can repair its declared helper dependency. Loop guards stay protected.
Attempt history retains candidate hashes, validation stages and findings;
rejected attempts are shown separately from current findings. Reaching a later stage
counts as progress even if it exposes more errors. A new helper's missing JSDoc may
remain as a targeted finding when a repair fixes existing defects; removing a contract
from a previously valid function remains a regression. Runtime findings are mapped
back to stable workflow/node fields, including identically named nodes in different
workflows. Scenario failures retain the failing step and execution cause. Exhausted repair
enters durable recovery, and waiting never consumes active planning time.
Loop item contracts are checked at the loop's exact input coordinate before scenario
setup. An untyped computation must be replaced with a typed array binding or a
validated transformation producer; it cannot become a generic graph-level repair.
Sequential continuation uses nullable `loop_previous` bindings derived from the
declared child result schemas. They are available in the loop's `while` condition
and body, and are null before the first iteration. Lowering owns their runtime
addresses. Parallel loops and references outside the owning loop cannot select them.
Loops driven by MCP observations use encrypted, schema-validated synthetic response
sequences instead of constant samples. Nominal execution must consume the entire
sequence and terminate; an extra request or early exit remains inconclusive. Failure
and cancellation scenarios still inject their faults before returning a fixture.
These fixtures establish synthetic control-flow coverage, never live external results.
An evidenced semantic finding about missing runtime observations targets the affected
operation's `/preparation` location. Bounded reassessment retains answers, usage and
discovery, discards invalid inventory and derived construction, and requires fresh
behavior approval. Independent behavior and implementation findings remain active
through reassessment and restart; an exhausted reassessment displays all current
findings. Machine diagnostics remain separate from user-intent evidence.
Final semantic review receives the selected capabilities' authoritative input and
output schemas, keyed by the graph's capability IDs, with identical schemas shared.
Locked argument changes require preparation reassessment. Review must not require
an undeclared argument based on assumptions about an external API.
Observation reassessment sees converted candidate inputs, including deterministic
locked bindings. Unconverted skeleton inputs are marked unresolved and cannot by
themselves establish a missing capability.
Computation validation checks statically named fields on typed parameters and simple
aliases against their producer schemas. Opaque results permit whole serialization,
not invented `.text` or `.json` projections. Dynamic JavaScript still requires runtime
and semantic validation. Retried units refresh their findings before constructing a
repair request, so newly detected contract defects are included without an extra call.
Switch selectors with known finite outcomes are checked against their accepted case
labels. A confirmation returns a boolean; presentation labels cannot replace the
explicit mapping from that boolean to business outcomes.

`PlanningValue.ResultChannel` is optional: null/`default` retains legacy result addressing,
while `structured` selects validated `mcp.call`/`llm.call` structured-output `.json`.
The compiler validates that channel's declared schema and referenced fields. Original
MCP response fields continue to come from the tool's declared output contract. Merely
listing required field names does not establish a structured-output schema.
Construction wraps a bare literal fallback in `json` only after its value satisfies
the complete structured-result schema. Explicit envelopes are preserved; missing
fields, wrong types and computed values still require validation and repair. This
adds runtime addressing without inventing fallback content or changing error actions.

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
[data binding and field repair contracts](../../docs/planner-v2-data-bindings.md),
[evaluation corpus](../../evaluations/workflow-planning/README.md), and
[published smoke exception audit](../../tests/GnOuGo.Flow.Planning.Smoke/README.md).

Capability inference failures in v2 pause in durable recovery with operation-level
findings. Rejected matching repairs remain separate from the retained candidate.
See the [capability recovery diagnosis and next architecture step](../../docs/planner-v2-capability-recovery.md).

Planner v2 decision contracts, confirmation routing, recovery and live validation are described in [Planner v2 decisions](../../docs/planner-v2-decisions.md).

Completed loops expose compiler-owned artifact collection bindings when their original MCP producers declare a JSON-array encoding. Models select the exact binding identifier. A missing artifact binding pauses before dispatch and checks whether the locked capability catalog changed; Retry after a catalog change preserves answers and usage but requires renewed behavior review.

Opaque MCP producers with declared downstream operation dependencies must establish a validated structured result before consumer implementation. Contract generation receives the owned producer, declared consumers (including across workflow boundaries), and enclosing control-flow obligations, without unrelated nodes or implementation instructions. The request, retained answers, host constraints and technical findings remain available. Ordinary generated bindings then use the declared structured channel. Whole raw-result bindings remain available in the general contract index for serialization; adding an untyped raw object to a synthesized schema is invalid. Missing producer contracts are repaired at the source, before scenario failures can trigger repeated parser changes in consumers.

Schema diagnostics identify the invalid nested declaration. Repair patches expose
only those schema coordinates and retain valid sibling fields and constraints.
Producer contract generation includes its declared consumers' complete argument
schemas, with shared definitions and separate host-bound values. Generated argument
names identify the remaining data needs without dropping schema constraints.
For composite operations, contract context also identifies sibling producers and
their authoritative or already-validated result schemas; one read must not recreate
all other reads’ contributions.
After behavior revision, schema generation receives only retained schema findings
for its stable node identifiers. Implementation and routing findings remain stored
for their own phases; free-text revisions without validated coordinates remain intact.
Invalid opaque object declarations may use a validated text representation when
consumers do not require internal fields; unknown fields never become bindings.
New MCP nodes with declared object fields reuse their authoritative results directly,
except when a locked decision explicitly requires a separate structured result. Such
producers must establish the exact decision field and finite outcomes before routing
is constructed. Contract prompts include these obligations, and missing or incompatible
fields are repaired at the producer. String-enum selectors are checked against accepted
case values before final semantic review; correcting computations cannot rewrite routing.
Additional interpretation requires an explicit transformation; existing structured
declarations remain compatible. Empty native container contracts and required null
annotations are constructed by the host. Missing model-owned fields and malformed
explicit values still require validation and repair.
Retained-candidate validation uses the same helper dependency checks as new responses.
An unchanged invalid candidate with no model dispatch pauses instead of repeatedly
writing identical recovery attempts.

Before initial behavior review, the host completes a missing decision default with
an explicit empty no-action outcome. This applies the existing fallback policy;
it preserves every explicit case and never erases an invalid supplied default.
The completed plan receives its own review hash and still requires approval.
Missing locked finite outcomes continue to block review.

Construction schema references are scoped to owned contracts, declared upstream
results and direct external-consumer inputs. Existing schema references on retained
nodes remain selectable. Unrelated catalog entries and native configuration fields
do not become new synthesized-result options merely because they share a workflow.

Selector repairs receive exact accepted case labels and outcome descriptions,
including whether the retained default has no actions. Unreachable-outcome findings
name both actual and accepted labels; a repair never has to guess routing targets
from a producer's boolean schema. Accepted cases remain outside editable patches.

An empty unbound sequence in the reviewed plan lowers to a native empty-object set.
It retains the node identifier and no-action result while satisfying the executable
DSL's non-empty sequence requirement. This lowering requires set in the locked
allowlist and does not rewrite bound operations or the approved behavior graph.

Nullable typed enums export null in both the type union and allowed values. Import
preserves the intersection of those constraints: a source enum that excludes null
does not become nullable merely because its type union includes it. Literal nulls
are checked against the complete destination contract before compilation.

Focused behavior repairs must change the diagnosed structure or dependency contract.
Changing only purpose or outcome descriptions cannot repair iteration, ordering or
routing. Such candidates receive the remaining targeted repair call before review;
exhaustion preserves the original plan and pauses in recovery.
Retry also detects historical repairs whose diagnosed nodes and enclosing topology
remained unchanged. It preserves other findings by stable node identity and returns
to behavior repair and renewed review before dispatching more construction calls.
When a new unbound loop reuses its unchanged child operation's key, the host assigns
the wrapper a deterministic unused identifier. The original producer keeps its key,
capability and dependencies. Ambiguous collisions or altered producers are not renamed.

A single producer contract that reaches the output ceiling automatically tries flat
typed schema declarations once before recovery. The switch is checkpointed before
dispatch and also applies when retrying older retained failures. The host assembles JSON Pointer paths into the existing schema
contracts and runs the same validation before accepting the unit. Raw declarations,
conversion findings and repair counts survive restart; repairs preserve valid rows.
The model, input/output ceilings, compilation gates and behavior approval stay intact.

Construction contract 36 traverses nested alternative schemas when resolving data
paths and exposes a field only when every alternative declares it. Continuing
structured fallbacks retain their declared `.json` contract because Flow.Core
validates the resolved fallback before downstream execution. Computations can
produce these fields; invalid computed values stop execution and still run cleanup.
Other envelope fields, including the original `response`, remain separately
derived from their producers and fallback declarations.

Construction prompts share repeated container-contract objects through a local
context table when doing so reduces request size. Expanding that table reproduces
every original value, constraint and JSON Schema reference; executable schemas and
the 12,000-token input ceiling remain unchanged. Literal reserved reference keys
disable sharing rather than being reinterpreted.

Construction contract 37 retains explicit operation ownership when intermediate
producers execute inside owned iterations. The final direct consumer must consume
those complete container results and every locked upstream dependency. Iteration
helpers do not acquire unrelated argument requirements from the composite
operation. Conditional, mixed-ownership, and human-input nodes do not qualify for
this dependency grouping; artifact provenance and permission checks still apply.

Construction contract 38 encodes binding identities as compact base64url strings,
retaining all 80 fingerprint bits. Restored implementation and public-output
candidates accept the earlier hexadecimal identifiers only when the same producer
is still available. Literal text is unchanged; unknown bindings remain invalid.
This reduces repeated response-schema enums without removing binding contracts or
increasing request limits. Checkpoint upgrades revalidate candidates and preserve
encrypted history, approvals and cumulative usage.

Construction contract 39 runs Core's generated-function documentation check before
accepting a helper unit. Later findings route back to that unit. Documentation-only
repairs receive its functions and findings, without unrelated capability contracts,
and must preserve every executable declaration and statement exactly. Repaired
helpers update both the checkpoint and assembled workflow; other helpers and node
inputs remain unchanged. The normal repair, context and output ceilings apply.

A transport failure still enters recovery without an automatic provider retry.
Explicit Retry of an unreceived single-producer contract selects the equivalent
flat schema transport. This can reduce construction complexity; it does not prove
provider availability or release the previous request's uncertain usage reservation.

Construction contract 40 revisits contributing synthesized producer contracts when
an implementation repair still cannot bind a locked upstream operation. The review
is checkpointed before dispatch and uses the existing consumer argument schemas.
It may add validated fields while preserving every existing declaration; a producer
that cannot supply the missing observation must retain its schema. Original catalog
results, topology, effects and accepted behavior remain locked. Consumers resume
only after their prerequisites validate, and must still pass dependency validation.
Repeated identical candidates cannot repeatedly schedule the same producer review.
Known extra result-type mismatches and unresolved loop item contracts can also revisit
the explicitly referenced synthesized producer after a targeted implementation repair.
Rejected consumer candidates and their findings remain unvalidated checkpoints; they do
not replace the retained graph. Additive contract review preserves existing declarations,
then resumes the dependent implementation queue. Original catalog schemas remain immutable.
Established extra value contracts can supply new result properties without a model call
when their full schemas survive an exact round trip; unsupported constraints and opaque
values remain unresolved. Additive model repairs use flat declarations under exact root
coordinates, avoiding recursive schema responses and excess transport nesting. The host
assembles at most four new properties, validates them and preserves the existing schema.
Undeclared computation fields receive targeted repair before early observation reassessment.
Encrypted checkpoints record the exact received candidate, findings and dependency fingerprint;
old repair totals, unreceived calls and changed producer contracts cannot trigger escalation.
Native loop response schemas distinguish initial inputs from the `while` condition.
The loop's own previous result and index are selectable only in its condition/body;
ancestor loop state remains available in a nested body. Targeted repairs retain this scope.
An oversized optional early assessment is retained as a deferred finding while field repair
continues. Complete semantic review and required scenarios still gate final approval.
Review baselines, cumulative calls, pending consumers and history survive restart
inside the encrypted schema-version-2 snapshot.

Construction contract 41 limits direct MCP argument bindings to producer types that
fit the destination contract. Identical binding sets share one schema definition;
explicit transformation parameters retain the complete source index. Nullable
arguments and omitted optional arguments remain distinct. Mistyped arguments do
not satisfy locked operation dependencies. A diagnostic preview collects these
related findings together while retaining all other response-shape checks, so repair
can move a dependency to an appropriate argument instead of alternating between
incompatible candidates. Artifact arguments retain their stronger provenance rules.

Semantic review can address a decision's `expr` directly. Incorrect selector values
remain executable-field repairs and preserve accepted outcomes, rather than forcing
a topology revision that cannot correct the computation.

Construction contract 42 rejects provably unreachable named switch outcomes before
unit acceptance. It derives finite labels from string/boolean literals, comparisons,
negation, conditional expressions and direct function returns without executing code.
Unknown helpers and dynamic values remain unknown. Repairs target `expr`; accepted
case values, defaults and topology remain unchanged.

Construction contract 43 exposes each declared field of a non-nullable, closed `set`
result as an exact `values` coordinate. Lowering emits an object whose individual
values retain their typed dependencies; root computations cannot substitute opaque
objects for field provenance. Optional fields distinguish omission from null. Large
objects use checkpointed groups of at most four value coordinates, within the existing
token ceilings. Legacy explicit fields upgrade without changing their values. Opaque
legacy computations remain in encrypted revision history and require field generation;
unrelated implementations, accepted behavior, answers and cumulative usage remain.

Behavior revisions discard executable-candidate repair progress while retaining
cumulative usage, answers and attempt history. Newly approved behavior starts unit
construction. Retry repairs older stopped sessions that have no executable units;
it does not dispatch an empty patch because an earlier candidate had exhausted repairs.
Manual YAML edits still enter complete artifact validation.

Producer contract review also covers a native transformation whose implementation
cannot consume its accepted business inputs. Only its extensible inline result schema
can gain justified fields; catalog contracts and existing field constraints remain
immutable. Contract prompts include the accepted input dependencies. After a schema
addition, repair selects the missing value coordinates before reassessing semantic
dependencies, preserving already valid calculations.

Schema extension responses contain only `addProperties` (at most four justified new
properties per response). The host merges them into the retained baseline, preserving
existing schemas and annotations exactly. Duplicate or replacement names fail
atomically. This response contract also repairs older rejected candidates without
requiring the model to reproduce existing declarations.

Construction contract 46 rejects direct item-field access on typed array parameters
and simple aliases, while permitting array operations and explicit element access.
Numeric parameter indices retain the declared item schema through aliases and
null/empty-object fallbacks; guessed item property names remain invalid.
Discarded parameter reads (`void value` or `value;`) do not establish computation
dependencies. A retained collection mismatch can trigger a bounded, evidence-checked
behavior assessment; a required missing loop returns through behavior review without
repeating capability discovery. Existing observation-only assessments remain scoped
to preparation. This static validation does not prove arbitrary JavaScript semantics;
scenario execution and request coverage review remain required.

Semantic review receives the resolved contracts for references actually consumed by
the graph, including container response envelopes and structured channels. Logical
node keys remain compiler-resolved references. Unresolved contracts are identified
explicitly instead of being presented as empty or invented producer schemas.

Retry checks the current catalog before reusing a rejected implementation. Changed
contracts invalidate the retained capability selection and behavior approval while
preserving answers, history and cumulative usage. An unavailable catalog keeps the
session in recovery without dispatching model requests.

Computation dependency validation resolves JavaScript lexical scopes, including
callback parameters, destructuring, block declarations, function-scoped `var`,
and catch bindings. Reusing a typed parameter name in an inner scope is allowed
when the outer binding is actually used. Inner references do not count as use of
the outer input, and out-of-scope declarations do not authorize context access.

Repeated missing business-input bindings can trigger a scoped semantic assessment
of the accepted dependency and the selected capability's argument contract. An
evidenced assignment error returns to behavior revision and requires new approval;
ordinary omitted bindings remain implementation repairs. Successful dependency
revisions retain fingerprinted evidence so Retry distinguishes the approved contract
change from a presentation-only edit. Neither this assessment nor its repair may
remove a requested use of an input or manufacture dependency through an irrelevant
argument. Unchanged requirements, confirmations, and cleanup remain locked.

Early behavior assessment receives declared capability argument names, descriptions,
types, and requiredness before assigning business-input dependencies. Recovery
assessments include exact consumed references and bounded type summaries; transitive
business-input dependencies are explicitly distinguished from argument values. This
scoped dependency review can request behavior revision only. Omitted producer schema
fields cannot justify capability rediscovery; observation assessments remain separate.
