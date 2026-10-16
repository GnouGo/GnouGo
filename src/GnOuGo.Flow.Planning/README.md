# GnOuGo.Flow.Planning

A separately publishable package depending only on Flow.Core.

Explicit `requires` remains a local fail-fast runtime assertion, including inside conditional paths. The compiler validates types, scope, availability and contracts; it no longer attempts conditional implication proofs or automatic guard strengthening. Branch conditions govern optional routing. Automatic repairs cannot remove or alter `requires`; explicit targeted revisions remain available. Retained guard-proof repairs require revision after accounting for any completed original receipt; unknown completion remains stopped.

Fresh `compact-bindings-v12` removes proven singleton wrappers and redundant JavaScript declarations while retaining every ordered check in `expression_contracts`. Scalar, array and object locals use native multiline code; validation-only aliases reference already evaluated locals. Output expressions can carry the same checks followed by final schema validation. Outputs still execute after finalization: sets with independent validation, shared-state, branch or receipt responsibilities remain. Existing input/workflow spans identify checks and original origins without copying observations. Inference boundaries, strict lookup, exact JSON values and sandbox ceilings are unchanged. Omitted/v1–v11 profiles preserve exact lowering and approval reproduction. See [compact compilation](../../docs/compact-workflow-bindings.md).

`Requirements → progressive discovery → LLM TaskPlan → deterministic compilation → PlanningGraph → YAML → validation → scoped TaskPlan repair → approval`

Requirements are host-owned until explicit user revision. New sessions declare the caller input interface in `requirements.inputs`; null is unresolved during discovery, and a plan requires an explicit array, including empty for no caller inputs. Once accepted, names, types, requiredness and defaults must match the plan. Subsequent responses omit accepted requirements. Historical responses remain unchanged in storage. Unfinished sessions with a retired intent profile require explicit revision before dispatch.

Implementation-only review corrections use `revise` with optional `preserveRequirements: true`. Add optional `editablePaths` for a targeted correction, for example `["/tasks/compose/inputs", "/tasks/publish/requires"]`. The command must identify the current revision and artifact hash. Existing binding and export paths, input lists and conditions are supported; duplicate, overlapping, ambiguous or structural paths are rejected before inference. The retained plan remains the baseline and the model returns a typed patch, not a regenerated TaskPlan. Unselected work and interfaces remain immutable; global restructuring still requires an explicit ordinary revision. This consumes the existing planning allowance, invalidates approval and grants no runtime permissions.

When reviewed adaptation needs task additions/removals, use the existing explicit structural revision with `preserveRequirements: true`, current revision/hash and a limited instruction. For example: replace whole-document interpretation with independent complete-page extraction, select offered identities, reconnect original records, and assemble already typed rows/status directly. Compare requirements, public output contracts and required operations/order against the baseline before accepting the proposal; remove only work explicitly excluded by that revision. Test the revision on a disposable session first. This is not automatic repair authority and does not authorize workflow execution.

Static expression validation retains the original `anyOf`/`oneOf` schema, including sibling properties/items, requiredness, `allOf` and local references. It checks complete alternatives using supplied literals and inferred types instead of selecting the first object descriptor. Unknown values cannot prove constants or unique membership; the existing runtime validator still checks the complete resolved request before dispatch. Do not reorder tool schemas or change valid bindings to conceal a static-validator defect. This correction does not alter v9 JavaScript or `expression_contracts` lowering.

Fresh private envelope 10 fingerprints the complete edit authority, including explicit revision paths. Automatic reference repairs retain the referenced producer: only compatible visible ports or authorized export chains may replace an invalid port. A missing producer returns `REVISION_REQUIRED`; a string or same-typed unrelated value cannot stand in for missing work. Explicit business literals remain valid. Historical envelope-7/8/9 requests retain their original schema/fingerprint and receipt recovery. Optional `EditablePaths` is omitted when absent; TaskPlan, PlanningGraph and storage format 10 are unchanged.
Supply the expected revision and exact artifact hash, or null when no artifact exists.
The accepted requirements remain immutable, discovery and cumulative accounting survive,
and the previous TaskPlan becomes the revision baseline. Generated artifacts and approval
are cleared. Omission retains ordinary intent-revision behavior and historical command
serialization. Pending inference must be reconciled first; revision grants no extra budget.

When evidence must survive failure, compose existing scopes: the outer `always` runs a
`sequence` whose body preserves available evidence and whose own `always` releases temporary
resources. This also attempts cleanup if preservation fails, while preserving the failure.
Do not depend on a failed normal task or invent absent evidence. Genuinely unknown external
completion blocks finalization. Bind caller input values explicitly; mentioning an input's
name in a literal instruction does not pass its value to an operation.

The existing loop also accepts an exclusive `clarifications` response before a plan or during repair. One to three material questions offer two or three alternatives with one recommendation, or text-only input for missing facts. Designer/chat share recommended selections and custom text; generic Flow hosts reuse human-input forms. Questions pause in auto mode too. `answer` saves an atomic revision-checked batch before continuation; declared literal selections still compile locally. Fresh interaction policy 1 preserves accepted intent through answers; a custom literal choice that changes the domain requires a separate explicit revision. Historical states retain their captured behavior. No answer approves execution, widens host policy or resets budgets. See [interaction contracts and validation](../../docs/planning-clarification.md).

`TaskPlan` is editable business intent. `PlanningGraph` is the sole executable representation. `HybridWorkflowPlanner` uses one bounded discovery/planning/repair loop; resolving selected operations and compiling them do not call a model. Static validation and simulations do not establish external success.

Tasks declare stable IDs, objectives, operation IDs, named business bindings and dependencies. Sequence is the default; concurrency requires a parallel scope or parallel iteration. Conditional alternatives declare matching business outputs. Reusable groups declare their inputs and outputs. Iteration has finite item and concurrency ceilings, preserves order and duplicates, and validates the collection bound before dispatch. Each scope can own `always` tasks.

`dependsOn` names eligible tasks in the same scope. Ordinary data bindings already
establish dependencies; available ancestors are captured without cross-scope
ordering entries. Descendant values pass through explicit exports at every boundary,
then consumers use the enclosing task's named output. Existing equivalent export
chains are reused. `present` checks an accessible task's completion, not a business
field or hidden descendant; an export never aliases an inner task identity.

Fresh dependency repairs enumerate only local, phase-compatible, noncyclic targets
and preserve unaffected edges. An inaccessible immutable `requires` reference stops
with `REVISION_REQUIRED` before another repair, unless existing authority can resolve
its data exports. Diagnostics and read-only repair context identify scopes, phases
and export names. Historical version-7 requests retain their original schema and
fingerprint; full semantic validation remains mandatory for every patch. See the
[scope-reference regressions](../../docs/scope-reference-planning-2026-10-05.md).

Task, group and choice declarations share one case-sensitive namespace throughout the TaskPlan, including nested scopes and reusable-group definitions. Their IDs and references use only nonempty ASCII letters, digits, underscores or hyphens, excluding the reserved `__` prefix. Repeated calls do not redeclare a group. Alternative IDs remain local to each choice; business ports, operation IDs and requirement IDs are outside this namespace. Invalid declarations fail closed without normalization, automatic renaming or broader repair permissions. Existing malformed consumer references can be corrected only in their diagnosed binding slots. Recovered plans and approval verification use the same identity rules; regenerate a saved plan with invalid declarations.

`TaskPlanCompiler` owns executor selection, stable generated IDs, request envelopes, references, declared branch merges, collection projections, explicitly supplied literal defaults and cleanup guards. Transient symbol tables and source maps are not another persisted plan. Compilation diagnostics address business tasks and ports. Generated executor validation failures stop as compiler diagnostics; the model is never asked to repair generated plumbing.

`ICapabilityCatalog` exposes source summaries, paginated versioned operation summaries and exact contracts. Ordinary business ports come directly from authoritative schemas. Injected catalogs may supply `PlanningOperation` mappings to exact schema fields; mappings never come from names, descriptions or examples. Registered executors explicitly opt into planning through `StepContract.PlanningEffectKind`. Only inspected, admitted exact contracts may be selected; they are revalidated before approval and execution. Discovery pages and resolved receipts survive repair; unavailable unrelated sources remain visible limitations.

`value` copies or assembles data; `transform` interprets named `inputs` using its `objective` and a declared `resultType`. Results must be nonempty closed objects with fully typed required fields, explicit nullable values, and no defaults or opaque types. The compiler emits fixed `template.render` prompt assembly and strict structured `llm.call`, using existing runtime configuration and budgets. The model never supplies templates, model settings or executor envelopes. Transform repairs edit diagnosed bindings or exact type slots without renaming business fields; replacing a `value` task requires explicit revision/regeneration.

Fresh composition guidance distinguishes performing the requested work from
generating its implementation as an artifact. For example, a requested review
workflow performs inspection, checks, review, authorized feedback and cleanup;
writing another workflow file is sufficient only when that file is the requested
deliverable. This is generation guidance, not a tool-name heuristic or a ban on
handwritten artifact-generation workflows. If accepted requirements already
describe the wrong deliverable, correct them through an explicit requirement
revision and review the resulting artifact; automatic correction cannot silently
replace that accepted intent. The verbatim-description guidance applies when
such descriptions are requested, rather than prescribing descriptions for every
business domain.

For large observations, declare a compact view for each consumer, using typed field selections or existing independent extraction. Avoid a shared superset containing all fields needed by later tasks. Keep full observations and exact action references in existing bindings; include a reference in interpretation only when that decision needs it. Explicit existing bindings and control flow reconnect decisions to the original action data; no runtime field removal is inferred from prose. Then bind global interpretation only to those results and necessary shared context. A complete bounded item can be a declared record; a whole page may be too large even for mapping examples. Preserve necessary source references, grouping, order and original completeness guards. Global comparisons and synthesis stay global; no runtime partitioning or automatic TaskPlan rewrite is performed. The review diagram shows transform mode, named input sources (including assembled bindings) and nested declared result fields. This makes broad collection bindings visible for review, without claiming that static validation proves semantic completeness. See [compaction correction and validation](../../docs/compact-interpretation-2026-10-05.md).

### Independent extraction declarations and producer repairs

`each.input` is the exact name in `inputs`, not the sandbox's `item` alias.
`resultType` describes the **assembled** result. This task produces one candidate
array per source page (the inspected `observe.pages` port is a collection):

```json
{
  "id": "adapt", "kind": "transform", "mode": "extract",
  "objective": "Extract unchanged IDs and complete text per page",
  "requires": {"kind": "output", "source": "observe", "port": "ready"},
  "inputs": [{"name": "pages", "value": {"kind": "output", "source": "observe", "port": "pages"}}],
  "each": {"input": "pages", "output": "rows"},
  "resultType": {"kind": "object", "fields": [{"name": "rows", "type": {
    "kind": "array", "items": {"kind": "array", "items": {"kind": "object", "fields": [
      {"name": "id", "type": {"kind": "string"}},
      {"name": "label", "type": {"kind": "string", "nullable": true}}
    ]}}
  }}]}
}
```

Here the declared producer `observe` exposes `ready: boolean` and complete `pages`.
Assert its real success/availability/completeness fields before dependent work;
`ready` is illustrative, not a conventional field or a compiler-inferred guarantee.
A false `requires` fails the task. Route optional work with a conditional instead
of sequential opposite assertions.

Predicates are binary except `not`: three declared producer flags use
`success AND (available AND complete)`, not three operands in one `and`:

```json
{"kind":"predicate","predicate":"and","items":[
  {"kind":"output","source":"p","port":"success"},
  {"kind":"predicate","predicate":"and","items":[
    {"kind":"output","source":"p","port":"available"},
    {"kind":"output","source":"p","port":"complete"}
  ]}
]}
```

This is the tested fresh-generation example, not a convention for producer field
names. Bind status from actual declared results and metadata without interpretation;
guard nullable snapshots before reading completeness. `field.port` selects exactly
one literal declared property: `available},{` is a different name from `available`.
Punctuation and Unicode remain valid when declared. An unknown name inside
`requires` needs an explicit targeted revision, never sanitization or removal of
the assertion. Correct references do not prove the rest of a plan meets its business
requirements; review extraction, original-ID reconnection and deterministic rows.

Bind `flatten(output(adapt, rows))` directly to the compact decision input.
Interpretation selects IDs only; use `lookup` against offered candidates, then
retained original records for action arguments. Preserve order and repeated IDs.
For example, the effectful consumer can validate offered identities in its own
assertion before resolving the exact same selected identities against originals:

```json
{
  "requires": {"kind": "predicate", "predicate": "not_equal", "items": [
    {"kind": "lookup", "port": "id", "items": [
      {"kind": "flatten", "items": [{"kind": "output", "source": "adapt", "port": "rows"}]},
      {"kind": "output", "source": "choose", "port": "ids"}
    ]}, {"kind": "null"}
  ]},
  "items": {"kind": "lookup", "port": "reference", "items": [
    {"kind": "output", "source": "retained", "port": "records"},
    {"kind": "output", "source": "choose", "port": "ids"}
  ]}
}
```

This excerpt belongs to an existing effectful `foreach`; its body performs the
requested operation. `id` is an unchanged copy of the original `reference`, never
a newly generated identity. Strict lookup rejects unknown or ambiguous identities;
the assertion does not manufacture a success flag. Export typed records and cell
arrays from their existing scope where contracts permit. Retain a projection when
it independently enforces a bound or captures values across scopes. No formatting
inference or identity-forwarding loop is needed merely to transport typed values.

A nullable selected ID requires an explicit `ID != null` branch before lookup.
On that branch validate `[ID]` against the offered candidates, then look up the
original records and project `field(item, "text")` in a bounded singleton scope.
The other branch exports `[null]` only where the accepted fact contract permits
absence. Both branches expose the same typed array interface. Lookup always
returns an array; neither `field(array, "0")` nor a learned copy is a scalar access.
Retain necessary captures when nested item scopes would otherwise shadow values.
One selected description identifies one complete original block, copied verbatim.
An interface control such as “report a problem” is not producer error evidence.

Prepend a supplied header with **`flatten([[headerRow], productRows])`**, not
`flatten([headerRow, productRows])`. For two illustrative columns, the writer's
existing `rows` binding is:

```json
{"kind":"flatten","items":[{"kind":"array","items":[
  {"kind":"array","items":[{"kind":"array","items":[
    {"kind":"string","text":"label"},{"kind":"string","text":"detail"}
  ]}]},
  {"kind":"output","source":"collect","port":"rows"}
]}]}
```

Here `collect.rows` is already `array<array<string|null>>`. The binding returns
the complete header followed by every row, preserving order, repetitions and
blank positions. No header task or formatting inference is needed. Type checking
cannot prove that a required business header exists: independently inspect the
written workbook, including zero, one and maximum-row fixtures. The retained v10
offline regression exercises this assembly through real Browser and Document MCPs.

A scalar per item has one assembled array, so it must not be flattened. Keep
complete original observations and their completeness checks separately. Typed
copying uses direct bindings. Do not add unrequested goals, side effects, filtering
or deduplication.

`choice` resolves a declared business choice; it cannot refer to an operation.
Use typed operation outputs for assertions; `present` proves only completion.
Separate fact extraction from acquisition status. For a producer declaring
`success`, a nullable `observationSnapshot` and its truncation flags, the tested
composition is:

```text
acquire → require producer.success AND snapshot != null
          AND !snapshot.captureTruncated AND !snapshot.manifestTruncated
        → extract observed facts only
        → typed output assembly:
            row = extraction.row
            status.success = producer.success
            status.availability = snapshot != null
            status.completeness = !snapshot.captureTruncated AND !snapshot.manifestTruncated
            status.message = producer.error_message
```

These are existing typed `output`, `field`, `object` and `predicate` bindings,
not a generated mapping script. Use only fields actually declared by the producer;
the names in this example establish no guarantees on their own. Acquisition
failure or incompleteness fails before extraction. Permitted null facts remain
null in an otherwise successful acquisition. `m.has` and `m.test` can control a
mapping but their computed booleans are not observed facts to export as status.
Never substitute an unrelated observed URL for a producer error message. The
offline revision and local workbook tests exercise this separation without
inference for the typed status or changes to accepted output contracts.

Invalid independent declarations and immutable invalid choice assertions stop
before a futile consumer-only repair, with `REVISION_REQUIRED`. Explicit
`EditablePaths` may select the diagnosed producer's `each`, `resultType`, inputs
and `requires`; automatic slots retain their existing authority. Read-only repair
context includes bound input names/references and producer contracts, without
observations or edit permission. Pending requests keep their original schemas,
fingerprints and receipts; receipt recovery precedes checks for new requests.

The parameterized [producer regression](../../tests/GnOuGo.Flow.Planning.Tests/ExtractionProducerRevisionTests.cs)
retains the failed and corrected compositions. The real
[Browser/Document fixture](../../tests/GnOuGo.Agent.Server.Tests/LocalProductOutcomeExecutionTests.BusinessTasks.cs)
executes an explicit three-slot correction and independently inspects the workbook.

String business types may include `enum: ["allow", "deny"]` (1–256 distinct strings). Omitting it preserves the unrestricted string contract; `nullable` remains independent. Generated structured results enforce these constraints before downstream calls. Diagnostics report the produced and required types and, when unambiguous, the exact producer enum/nullability slots implicated by its consumer. Objectives describing a decision never establish its finite domain. The final YAML validator recognizes runtime-checked structured results while rejecting unknown domains, optional/nullable selectors and unsafe error fallbacks.

Compact discovery entries expose declared `enum`/`const` constraints without full schemas. Resolved operation inputs constrain scalar literals in newly issued response schemas; typed references retain semantic compatibility checks. Index-only selections still resolve exact contracts before compilation. Identical schema fragments are shared without changing TaskPlan values. Successful discovery batches clear only their resolved response errors, preserving other findings, history and cumulative counters. See [enum generation and repair evidence](../../docs/planning-enum-discovery.md).

Catalog `FixedInput` and `RequestBindings` remain host-owned. Editable operation ports exclude their decoded paths; partial object assembly may supply unrelated fields. Explicit assignments (even equal values) and opaque ancestor bindings are rejected before lowering. The compiler injects the declared values, then existing graph/runtime checks validate the complete effective request. Resolved operation variants constrain input names; index-only selections receive the same ownership checks after exact resolution. Historical discovery receipts and pending schemas are unchanged. See [catalog-owned binding evidence](../../docs/catalog-owned-bindings.md).

Declared artifact metadata is retained in versioned capability summaries and presented as business ports and kinds in both compact indexes and exact contracts. Semantic preflight checks consumer bindings against the declared producer origin, including explicit scope exports and captures; matching strings or types are insufficient. Checked checked `set` mapping, per-item checked `set` mapping (ordered per-item selection) and identity validation preserve origin, with every possible projection path checked. Graph validation remains independent. Unconstrained capability results use the existing opaque schema representation when forwarded; they grant no typed field access. A resolved plan without a required producer reports `TASK_ARTIFACT_PREREQUISITE_MISSING`. Version-2 repair may insert a bounded chain only when exact policy-allowed contracts establish the missing producers; otherwise explicit semantic revision is required. Unknown/ambiguous contracts do not prove absence, and optional bindings remain removable under their contracts. See [prerequisite discovery and repair evidence](../../docs/artifact-prerequisites.md) and [artifact provenance regression evidence](../../docs/artifact-provenance.md).

A repair may explicitly omit a diagnosed optional operation argument when its exact contract permits omission. Null is not omission. All remaining bindings keep their values and relative ordering; required arguments, unrelated inputs and producer fields remain fixed. A diagnosed catalog-owned binding permits removal only, preserving unrelated members. Full request validation still enforces conditional requiredness. Producer-constraint findings expose only incompatible enum/nullability leaves, never automatic narrowing or invented values. See [the retained optional-input repair regression](../../docs/contract-aware-repairs.md).

`{ "kind": "field", "items": [{ "kind": "item" }], "port": "message" }` selects the declared `message` field of the current typed loop record. The same value works on typed inputs and business outputs; nested `field` values select nested objects. `port` is an exact field name, never a dotted/wire path. The compiler checks the authoritative object/field contracts and lowers the complete selection to the existing checked checked `set` mapping at consumption. Missing fields fail, null remains subject to the declared type, and opaque objects cannot supply fields. No transform, coercion, fallback or extra inference is involved. Selection preserves the exact nested transform type location through iteration and captures, so an incompatible enum diagnoses only that producer slot and its consumer binding. A type-only diagnostic does not authorize task insertion or unrelated changes.

`{ "kind": "json", "items": [<business value>] }` deterministically encodes one value as JSON text. The compiler materializes the value with existing typed `set` stages and invokes the existing `json` runtime function on that direct reference. It adds no inference, executor type or arbitrary expression syntax to TaskPlan. Encoding never grants access to undeclared fields of opaque outputs and is not a literal agent scope or choice alternative.

Discovery proposals use `discoveryRequests`: one to four page/query or exact-contract inspection requests, exclusive with a TaskPlan. An `operationIds` list selects already-discovered operations from its source, with `cursor` and `query` null; it replaces that source’s inspection selection, and an empty list clears it. Ordinary pagination preserves selections. This uses the same call allowance and grants no execution permission. The complete batch is validated before sequential metadata reads. Recovery reuses the recorded model response and identity. Superseded pending singular-source requests fail closed with a regeneration instruction and retained accounting. Contract fetching is deterministic; inspection selections use existing discovery responses, not an added model phase.

An optional `producedArtifactKind` on catalog paging and discovery requests filters the complete selected-source metadata by exact declared kind before pagination. New generation requests may automatically read at most one first filtered page per already-inspected source on each advance for uncovered prerequisites of selected operations, explicit inspections or the admitted shortlist; empty/unavailable results are cached, and expansion to newly found dependencies waits for a later advance. No read contacts an uninspected source. Matching search results have priority over unrelated optional contracts within the existing token allowance; they do not select executable operations. Filter/query/snapshot-specific cursors support ordinary bounded continuation and refinement. Pending requests retain their original schemas and metadata effects.

Selected sources are indexed from their complete tool listing before paging. Case/accent/identifier-normalized token overlap uses inverse document frequency (names ×3, declared field names ×2, descriptions ×1), with capability-ID ties. This ranking establishes no contract or permission. Catalog pages contain eight candidates; new prompts present a bounded directory of retained identities/port names and a token-budgeted global shortlist, plus TaskPlan-required and explicitly inspected contracts. Global ranking uses shared inverse document frequency over all retained discovered candidates, with no source quotas. Earlier-page candidates remain visible in the retained directory when space permits. The directory is budgeted against mandatory context before optional contracts; omitted entries are counted. Detailed contracts do not duplicate their index summaries. Closed discovery omits unusable source descriptions, queries and continuations. Conflicting versions cannot be automatically shortlisted or reused as an unambiguous selection. Query text defaults to the user request plus accepted requirements; a non-null query refines the search. The user request and accepted requirements always remain the global base. Each source adds only its own latest effective receipt query; overlapping terms count once. The historical batch query is retained for recovery but cannot replace the ranking base. Use a null query with continuation cursors, which bind to the effective query and metadata snapshot. Continuation eventually visits every tool; low relevance never means unsupported.

Full metadata and resolved contracts remain in session state. Prompts show coverage, the retained compact directory, exact active-shortlist contracts and contracts required by inspections or the current TaskPlan/revision baseline. Historical full pages are not repeated. Directory entries carry identities, names and business-port names; ranking still uses full metadata text. Optional candidates are admitted in relevance order only while the complete request, including its response schema, fits 90% of the saved input allowance (21,600 at 24,000). Oversized or unavailable optional contracts are skipped so smaller candidates can use the remaining space; omitted operations remain discoverable and require exact inspection before selection. Required contracts are never pruned; oversized mandatory context still stops admission. For fresh responses, shortlist resolution completes before the discovery checkpoint so new per-advance host adapters can reuse receipts. Recovering an issued response preserves its schema and discovery effects; optional presentation is recomputed before the next new request. Unselected resolved candidates never enter the executable catalog. Fixed-operation repairs omit unusable discovery indexes, alternatives and source descriptions, retaining coverage limitations and all required contracts. Pending requests retain their original prompts, schemas, numeric cursors where applicable, identities and budgets.

[Earlier deterministic discovery measurements](../../docs/budget-driven-discovery.md) compare the frozen coding/review requests; no live output-size or reliability claim is made.

New requests reserve one proposal call and one repair when repair allowance remains: with eight calls and two repairs, at most six calls may request discovery before the first TaskPlan. The repair limit is a maximum; earlier proposals may use both repairs if calls remain. Explicit zero-repair settings and smaller call ceilings remain authoritative. Repairs cannot use their final attempt for discovery. The issued schema closes `discoveryRequests` to null; it then permits `plan: null` as a terminal `DISCOVERY_INCOMPLETE` outcome. Returning discovery against that restriction stops with `DISCOVERY_NOT_ALLOWED`, without another metadata fetch or automatic repair. Historical schemas are retained for receipt validation, never reinterpreted or redispatched by the current profile. No call ceiling or accounting is reset. See the [retained exhaustion regression](../../docs/bounded-planning-discovery.md).


Explicit string literals retain their `const` contract through named values and scope captures. Runtime inputs (including inputs with defaults) and transform results keep their declared types. Producer-supplied patterns and string bounds are checked by the existing generic validators and appear in input diagnostics; Flow does not interpret tool names or filesystem rules. Declare a fixed resource location once and reuse its business binding for creation and cleanup, including partial failure. This guidance does not prove resource ownership or automatically reconcile independent paths.

Generation requests business tasks with concise objectives and direct consumer bindings. Inline typed copies, selections, assembly, flatten and lookup rather than naming forwarding tasks. Independent extraction can return one candidate array per complete page, consumed directly as `flatten(output(extract, rows))`; use a minimal pure foreach only for a necessary collection projection that existing bindings cannot express. Keep `requires` local and ordered, accepted contracts intact, and original action data separate from decision views. `value` remains supported for necessary independent materialization; `transform` is extraction or interpretation/conversion, never an adapter for typed copying. See [business composition examples](../../docs/compact-workflow-bindings.md#business-taskplans-not-a-task-per-binding). Nominal generation performs no post-generation task rewriting. Empty array literals retain their exact constant contract through compilation and graph validation; no element type is invented. The strict wire schema omits unused type fields, fixed transform-field declarations, host-owned choice selections and unused `explanation`. New wire schemas also omit `nullable: false`, `required: true` and absent `default: null`. Existing DTO defaults restore these representation constants only; `nullable: true`, optionality, array item types and actual defaults remain explicit. Every optional workflow/group input still requires a literal default. Representation compaction preserves TaskPlan semantics and planning storage format 10.

Fresh full-proposal requests use one worked composition guide. Its executable counterpart in the linked example and regression fixture connects complete pages to compact decisions, offered/original lookup, requested actions and deterministic header/row assembly. Available capabilities do not justify additional actions. Schema compaction retains five concise hints on task assertions, conditional routing, transform mode, independent extraction and assembled result types in one shared task-declaration annotation. These annotations change presentation only. Private repair schemas and authority fingerprints retain their historical construction, and pending requests retain their original prompts and schemas. Explicit structural revisions receive the fresh full-proposal presentation without expanding repair authority.

For requested descriptions, one complete observed features, characteristics or descriptive-text block can be relevant without a Description heading. Selection returns its unchanged offered ID, then strict lookup reconnects the original text. Multiple suitable blocks do not imply absence. Preserve permitted null when none is observable or its subject association is uncertain; never concatenate, paraphrase or shorten blocks or substitute interface controls. This guidance is sent on fresh generation and revision requests. It is not a semantic validator or an automatic repair trigger; deterministic fixtures and independent workbook checks cannot prove a future model's compliance.

Values allow literals, named business references, declared field selection, deterministic JSON encoding and closed typed predicates. An output reference uses a task ID and business port; a null port selects its whole business result. Opaque results have no typed fields. Null-only contracts lower through Flow's existing schema-backed port representation: the complete JSON Schema remains authoritative, including recursively nested nulls. No unsupported shorthand `type: null` or unconstrained fallback is emitted. Presence tests only whether a task produced a non-null result; it proves neither payload shape nor external success. JavaScript, executor types, wire paths, projection recipes and schema pointers are absent from the model response contract. Authored YAML retains the existing runtime language.

Generated agent stages require literal objective, nonempty workspace, capabilities, budgets, output contract and verification requirements. Agent workspace bindings may reuse available semantic `value` constants, including literal object assembly and field selection. Compilation emits a literal before approval; it does not establish filesystem existence. Runtime inputs, transforms, operations, choices and loop-derived values remain forbidden scope sources. Other scope fields remain literal. See [workspace and producer contract migration](../../docs/workspace-contracts.md). A business choice cannot select these scope fields. The injected runner still enforces paths, permissions, filesystem and inference policy.

`PlanningChoice` binds exactly one business value slot. It declares typed literal alternatives, a recommended ID and a host-owned selected ID. Interactive mode presents alternatives; auto mode validates and records the recommendation without another model call. Selection recompiles locally. Choices never approve execution or replace runtime confirmation.

The model schema restricts alternatives recursively to literals, requires at least two alternatives or parallel branches, and requires nonblank questions/objectives. It expresses item ceilings of 1–10,000, concurrency ceilings of 1–100, predicate arity (one for `not`, two otherwise), and array item types. Identity patterns use explicit ASCII alternatives and ordinary anchors without lookaround or engine-specific escapes. Tests cover JavaScript Unicode regex syntax and .NET; the compiler independently enforces exact lexical checks, including trailing line breaks where regex anchors differ. Semantic validation still enforces identity uniqueness, reference visibility, typed contracts and recommendation membership.

Permanent typed model-provider rejections produce `MODEL_REQUEST_REJECTED` with safe classification/status/code metadata, never a raw provider body. The Agent host blocks unchanged retries of these requests. After correcting the request/configuration, create a new planning session; retained requests and cumulative accounting are not reset. Unknown transport outcomes keep their existing recovery rules.

New request construction projects a clone of the completed generation or repair schema before token packing, hashing and persistence. Valid runtime patterns that are not portable to both JavaScript Unicode and nonbacktracking syntax are omitted only from this model-facing copy. Authoritative operation contracts, prompt context, semantic/MCP validation and repair authority fingerprints retain the original patterns. Strict preflight checks definitions, reference targets and nested schema positions; `MODEL_SCHEMA_INVALID` stops before reserving a call or repair. Invalid regex syntax and other unsupported shapes remain errors. Recovery uses the already-issued request unchanged. See [schema portability evidence and live commands](../../docs/planning-schema-portability-2026-10-02.md).

`MODEL_INPUT_LIMIT` instead stops locally before dispatch when the conservative full-prompt plus response-schema estimate exceeds the saved input allowance. No additional call or repair is consumed. Standalone Flow defaults to 12,000 input / 8,192 output tokens per request; new Agent.Server Designer sessions default to 24,000 input / 32,768 output, subject to explicit host overrides. Existing sessions keep saved limits, shown directly by Designer. Output budgets can include reasoning; serialization headroom does not guarantee generation completion. A revision-checked `configure_generation` command can resume a stopped session with no pending request and remaining call allowance, retaining discovery and cumulative budgets. Token-only changes and revisions cannot restart a call-exhausted session; Designer displays calls used / ceiling and directs the user to a new session. No automatic limit escalation, authoritative-contract truncation or model retry is performed; later calls still require budget admission.

Compiler preflight validates TaskPlan identities, scopes, business ports and contracts before emitting graph nodes. It collects independent diagnostics, including every invalid cross-scope consumer and required export boundary; unavailable prerequisite contracts suppress cascading errors. Children can capture available ancestor values. Parents consume explicitly declared exports; the compiler can route an equivalent descendant reference through an already complete export chain. Parallel siblings cannot directly consume each other. The compiler never invents exports or fallback values.

Conditional alternatives should expose the same consumer-facing ports with matching types, nullability and requiredness, bound to each branch's own values. For example, export a boolean `ready` and nullable string `reference` directly rather than forwarding two differently shaped whole objects. Object exports with compatible contracts and opaque pass-through remain valid; the compiler does not flatten or cast them. A consumer of `reference` must be on a path guarded by `reference != null`; testing `ready` alone proves no nullability relationship. Type diagnostics identify the relevant branch/export contracts when traceable through existing scopes. Changing both interfaces and downstream consumers requires an explicit revision when current repair slots do not authorize it. These diagnostics do not change compiler acceptance or repair permissions.


Repairs edit diagnosed business slots and the explicit export chains needed by their consumers. Conditional counterparts require explicit values. Revalidating dependent tasks does not authorize changing them; unknown locations never expand the scope to the whole plan. Unrelated tasks, interfaces, choices, ordering and compiled stages remain fixed. Rejected repairs retain their immutable baseline. Discovery, retries and revisions share cumulative budgets. Defaults remain eight model calls and two repairs. Approval hashes intent, choices, exact contracts, graph/YAML and ceilings. Approval verification recompiles and requires the reviewed artifact to match exactly.

For a conditional input contract, literal selectors may prove exactly one `oneOf`
branch. A failed complete-request check then diagnoses only incompatible business
bindings and their exact transform constraint leaves, including nested loop items.
Pattern-constrained strings may be repaired through an explicit finite domain;
patterns are never inferred or inserted into public TaskPlan types. Ambiguous or
dynamic selectors retain the aggregate failure and grant no branch-specific edits.
The full authoritative request is always revalidated.

Initial generation returns a full TaskPlan. Newly issued semantic repairs return a private `RepairPatch`: typed host-issued slots derived from the immutable baseline and existing revision scope. Each slot permits only its declared replace/add/remove action; null is never removal. Ordinary binding, constraint, dependency and export slots cannot change tasks. Version-7 structural slots may insert at most eight required-artifact producers immediately before the diagnosed consumer, replace a diagnosed invalid leaf with an operation preserving its identity/objective/dependencies and existing input bindings, or remove a diagnosed pure forwarder through equivalent host-owned reference rewrites. Every inserted producer must supply the diagnosed artifact or a declared required artifact of that chain; unused additions and unrelated dependencies are rejected. Unrelated tasks and ordering remain fixed. The host applies edits to a clone, checks the existing revision permissions, then recompiles and validates the entire plan. Unknown/duplicate/conflicting slots and unauthorized changes fail atomically; empty or unchanged patches stop without progress.

Repair prompts contain affected task fragments, immutable objectives, relevant contracts and necessary scope/dependency context. Read-only producers contribute exact output contracts and relevant fixed creation arguments without granting edits. The complete baseline and discovery journal remain host-owned. Binding-only repairs omit discovery navigation; operation-changing repairs retain the existing bounded discovery action. The saved request binds its complete typed permissions to baseline/scope/contract/policy fingerprints. All new repairs and targeted revisions use envelope 10. Its value slots support the existing `flatten` and `lookup` bindings; schema, context, authority and patch application use the same versioned vocabulary. Slot restrictions and whole-plan validation remain mandatory. Already-issued version-7/8/9 requests retain their original schemas and authority. Retired pending proposals require explicit revision; original schemas and identities validate saved receipts for accounting without applying their proposals. No planning storage-format migration is needed. Budgets and hard admission checks are unchanged. See [bounded patch evidence](../../docs/bounded-semantic-repairs.md).

Optional workflow and group inputs require literal defaults; optional fields inside an object do not. Independent input errors are reported together, with group-qualified locations. Discovery responses select uncached query pages or refine an inspected source with more candidates; small completed sources may still be refined when their tools fall outside the global shortlist; empty or unavailable sources offer no continuation. Composite scope outputs are assembled by typed `set` stages after cleanup, preserving their nested contracts. Successful checked `set` mapping and per-item checked `set` mapping (ordered per-item selection) stages provide non-null result envelopes for guarded export assembly, including when their checked payload is nullable. Failed, skipped, cyclic or unsafe continued producers do not establish availability. Repair rejections identify the changed business slots without accepting the rejected proposal as a new baseline. Presence sources remain exact task IDs, not dotted payload addresses.

Planning storage format **10** rejects prior planning sessions and approvals without modifying their encrypted records. Execution journal schema **9** is unchanged. AI revision requires a saved TaskPlan or fresh requirements and renewed approval; YAML import into the planner has been removed.

Consuming an optional operation port emits a checked checked `set` mapping at the consumer. The full authoritative container crosses scope boundaries; the check stays inside the consuming branch, iteration or cleanup. Missing fields fail, nullable values remain nullable, and unused ports need no projection. Optional-port checks require no fallback or model call; explicit nested business-field selection uses the `field` value described above.

Operation results use the same effective contract during semantic validation, lowering
and graph validation. For `agent.run`, the approved literal `output_schema` specializes
the envelope's `output` member. Authoritative unknown contents are represented by the
existing opaque marker inside their known object/array shapes. This permits whole-value
exports, captures and collections without inventing field types or changing nullability,
requiredness or constraints. Discovery contracts and issued requests remain unchanged.
Successful agent result envelopes also establish guarded export availability; skipped,
failed and continued producers do not. Schema errors report the nested schema pointer
and, when available, the business producer. See the [retained failure and execution
regressions](../../docs/operation-output-contracts.md).

```bash
dotnet build src/GnOuGo.Flow.Planning -c Release -warnaserror
dotnet test tests/GnOuGo.Flow.Planning.Tests -c Release -warnaserror
dotnet run --project tests/GnOuGo.Flow.Planning.Smoke -c Release
dotnet pack src/GnOuGo.Flow.Planning -c Release
```

The eight frozen business requests and independent execution oracles remain in `tests/Shared/PlanningBenchmarkCases.cs`. Small synthetic fixtures keep planner/AOT tests independent. Host product-query tests replay unchanged historical model responses against saved request schemas with actual Browser/Document registrations and real XLSX writing. A separately labelled synthetic compact response tests serialization headroom and the same independent business outcomes using direct query binding and scalar URL iteration. Neither fixture is supplied to live planning; synthetic results are not model reliability evidence. See the [contract diagnosis and replay limitations](../../docs/planning-product-contract-diagnosis-2026-09-26.md). Historical cohort evidence remains 8/9, without a new reliability claim. Real Copilot command edit/test execution remains unverified under the available sandbox policy; do not bypass it. See [architecture and migration](../../docs/workflow-planning-v9.md).

Repository agents must use `$gnougo-planning`, the portable [planning skill](../../.agents/skills/gnougo-planning/SKILL.md), as required by the root [AGENTS.md](../../AGENTS.md).

See the [typed record-field diagnosis and deterministic replay](../../docs/taskplan-field-bindings.md) for the retained review failure, corrected binding contract and execution limitations.

Explicitly requested inspection contracts are never silently pruned. Unknown, ambiguous, cross-source or policy-excluded IDs invalidate the whole batch before fetching. An unavailable requested contract stops with `DISCOVERY_CONTRACT_UNAVAILABLE`; mandatory context exceeding the saved ceiling stops through `MODEL_INPUT_LIMIT`. Pending requests keep their original schemas and identities. See [targeted discovery evidence](../../docs/controllable-discovery.md) for retained failures, deterministic results and Designer inspection.

`PlanningPrompt` owns deterministic request presentation and token packing, reusing ranking, required contracts and schema fragments within each request. `PlanningDiscoveryContext` owns metadata reads and versioned receipts. `HybridWorkflowPlanner` retains session transitions, model calls, recovery and approval. The [execution benchmark](../../tests/GnOuGo.Agent.Planning.Benchmark/README.md#planner-stabilization-comparison) compares eleven frozen cases, including actual local MCP execution.

Input type origins are tracked through checked bindings during semantic preflight.
A nullable input consumed as a non-null array produces an exact `TASK_INPUT_CONSTRAINT`
leaf alongside its consumer diagnostic, including nested fields and reusable groups.
A typed patch may change that nullable leaf only when accepted requirements permit it;
the input name, requiredness, defaults and remaining type stay fixed. Full compilation
validates the patch, and ambiguous display identities never grant broader permissions.

## Business intent and compatibility

Fresh sessions use `IntentVersion=3`, independently of compiler profile v15. Requirements keep their summary, outcomes and accepted public interfaces. An outcome may also declare `commitments`: exact inspected operation IDs, optionally with an exact producer result port supplying an accepted public output. These are obligations, not task IDs, scheduling, predicates or model-authored proof mappings. `OutcomeBindings` remains historical. Existing intent-version 2 sessions retain their captured behavior, schemas and approvals; omitted metadata leaves historical serialization unchanged.

```json
{"id":"deliver","description":"Save the requested report and return its location",
 "commitments":[{"operation":"<exact inspected operation ID>",
                 "result":{"port":"location","output":"filePath"}}]}
```

During discovery a null commitment list can be completed without changing the accepted prose or interfaces. Before the first accepted TaskPlan, every supplied commitment must resolve to an inspected contract and declared result/output ports. Thereafter commitments are immutable during automatic correction. Null or empty commitments leave coverage explicitly unverified; the planner never infers obligations from its generated plan.

Fresh intent-version 3 proposal prompts reuse lossless schema factoring for repeated read-only operation contracts. Their JSON Schema references resolve through the prompt's `schemas` dictionary. Existing scoped references and literal examples/defaults retain their original meaning. Catalogs, executable contracts, repair authorities and issued requests stay unchanged; the complete serialized request still passes the existing admission check. Commitment instructions live beside their response-schema declaration instead of being repeated in prose.

After compilation, coverage reports **verified clauses**, **violations** and **unverified aspects** through existing validation results. Reachable operations and supported direct bindings/exports/collection provenance establish contractual clauses. Uncalled groups and simulated effects cannot satisfy an operation commitment; literals or disconnected inferred outputs cannot establish producer-owned results. Ambiguous repeated operations, conditional or potentially empty execution and unsupported transformations remain unverified. The executable catalog still contains only selected operations; commitment contracts remain bounded read-only correction context.

Operation presence does not prove correct arguments, relevant extraction, per-product work, successful effects or complete prose coverage. Every requirement retains a limitations entry. Independent execution oracles remain necessary. No correspondence is inferred from task names, descriptions or compatible types.

New approvals require an explicit `reviewedRequirementIds` array containing each accepted requirement ID exactly once, alongside the existing expected revision and artifact hash. Confirmed violations block approval; coverage is recomputed during approval verification. In intent version 3, explicitly acknowledging an unverified requirement permits human review but never changes its coverage label. Designer checkboxes and `workflow.plan` human-input forms never preselect these acknowledgments. Successful acknowledgments are retained as `human_reviewed` results; revisions clear them. This is a human review gate, not automatic semantic proof. API clients and evaluation harnesses must collect explicit review rather than fill the array automatically.

TaskPlan/PlanningGraph versions and storage format 10 are unchanged. Optional transform mode is described below. Historical annotation fields remain readable and hash-identical. Already-approved artifacts retain approval; unapproved legacy artifacts require fresh generation and approval. An explicit revision keeps discovery, history, baseline and cumulative accounting. If a pending legacy request has a receipt, the Designer validates the original identity/schema and settles usage before revision, without applying the proposal. Unknown completion remains stopped for reconciliation. Other hosts receive `PLANNING_REVISION_REQUIRED` through the existing diagnostic/command channel.

See [simplification, measurements and local execution](../../docs/planner-simplification.md). Historical benchmark evidence is unchanged; live evaluation requires explicit campaign authorization.

## Runtime primitive consolidation

Foreach bounds and output collection use whole-value and per-item checked `set` mapping; confirmation uses checked `set`. The four previously retired .NET primitives stay removed. `value.project` is now retired too; one generic bounded runtime extraction executor replaces it. No planning phase is added. See [migration and validation](../../docs/flow-runtime-primitives.md).
# Contract closure

Business graphs retain typed predicates, JSON encoding and logical bindings until
complete input/output contracts pass validation. Executable mappings are emitted only
during final lowering. Opaque bindings use approved bounded runtime extraction against declared targets; explicit interpretation remains a business transform. See [contract closure and validation](../../docs/planning-contract-closure.md).

See [compiled and bounded runtime mappings](../../docs/runtime-mappings.md) for optional extraction mode, runtime inference review, encrypted cache, recovery and the breaking projection migration.

See [compact observations, accepted outputs and resource lifecycle](../../docs/compact-observations-and-execution.md) for the current producer/compiler boundaries and executor simplification.

## Ordinary arithmetic and decimal boundaries

TaskPlan `arithmetic` values describe closed formulas using `text` (`add`, `subtract`, `multiply`, `divide`, `remainder`, `negate`) and `items`. Operand contracts must be numeric and nonnullable. The compiler retains structured values through contract closure, then emits JavaScript into existing checked `set` steps. There is no numeric executor or runtime inference. Exact business arithmetic is an MCP responsibility; explicitly declared decimal fields are converted automatically at the MCP boundary. See [migration and validation](../../docs/numeric-compilation.md).

### Required conditions and finalization

A task may declare `requires` as a boolean TaskValue. Omission preserves historical behavior. New strict provider responses use `requires: null` for omission; persisted TaskPlans omit the absent field. The compiler checks the condition with the existing `set` output schema before task inputs, inference or effects. False fails rather than skipping the task; normal finalization still applies. Keep both conditional exports explicit: require completeness before a publishing sequence instead of inventing an output for an incomplete branch.

`present` can inspect preceding tasks in the same or a lexically enclosing scope. Nested finalizers capture availability independently and retain an absent payload as omission inside compiler-owned switch envelopes. They never eagerly evaluate an unavailable result. Report observed evidence in `always` and put release operations in its nested `always`, so preservation failure still permits cleanup. Unknown completion continues to block both. Required conditions cannot be weakened by scoped repairs; use an explicit revision and fresh approval.

The approval-fingerprinted `compact-bindings-v3` profile introduced options for fused typed copies with original indices, normal-path exports, literal descriptions, isolated business loops and unambiguous runtime extraction. Historical profiles retain their lowering. Declared collection bounds never enlarge effective host limits. See [profile and compatibility](../../docs/compact-workflow-bindings.md).

### Explicit consumer views and flatten

`flatten` is an additive TaskValue/PlanningValue binding with exactly one operand in `items`. It accepts typed, nonnullable arrays of arrays and concatenates one level in source order. Element nulls, duplicates, deeper arrays and exact references survive; null inner arrays and opaque elements fail. Compiler-owned checked selections enforce source constraints before concatenation and consumer constraints afterward. Extraction result contracts and explicit object/field bindings define closed consumer inputs. Global interpretation still has to fit complete-request admission; flattening a broad result does not compact its fields.

No new compilation profile is introduced. Plans without the new binding retain historical lowering under omitted, v1 and v2 profiles. Already-issued schemas and repair authority remain unchanged. New envelope-10 repairs expose flatten only within authorized value slots; historical envelopes retain their original vocabulary. Review identifies the explicit one-level binding. See [runtime mapping](../../docs/runtime-mappings.md) for consumer-specific candidate views and independent extraction.

### ID-only selection and lookup

For decision-driven actions, declare the extract `resultType` as candidate identity plus facts needed by that consumer. Bind only this view to interpretation. Keep complete original observations and authoritative action arguments separately. The compiler never infers relevance or drops fields. A necessary oversized view still fails request admission.

`lookup` is a closed TaskValue binding: `kind: lookup`, literal `port: identityField`, and `items: [recordCollection, selectedIds]`. It returns original records in selected-ID order, including repeated selections. Missing/null/blank keys, ambiguous duplicate source identities, unknown selections and incompatible types fail with `CONTRACT_UNSATISFIED` before output publication. String comparison is ordinal; numeric identities must be safe integers, without string coercion. Empty selection remains empty, but does not excuse invalid source identities. Source and downstream schemas remain checked.

Use it only to reconnect selection: verify selected IDs against the offered candidate view, project those verified IDs, then resolve them against retained original records. Producer IDs are preferred; otherwise use the original collection index attached before extraction/filtering. Index identities belong to that captured collection, not a later snapshot. Never hash content or renumber surviving candidates. No model-generated action argument is used on this path; producer permissions, action compatibility and expiration still apply.

Lookup stays structured until final lowering into a checked `set` and compiler-only sandbox helper. It adds no executor, compilation profile or general lookup/composition framework. Learned mapping scripts cannot call the helper. Historical plans, schemas and repair authorities remain unchanged; fresh generation and envelope-10 repair schemas expose lookup, while restructuring outside existing repair slots requires revision. `json` produces text, not an executable binding: encoding a lookup or flatten description cannot substitute for typed data. Lookup diagnostics identify both operands, inferred types and producer references.

### Array cardinality and explicit transform revisions

`TaskType` arrays optionally declare `minItems`/`maxItems` (nonnegative, minimum no greater than maximum). Omitted bounds retain historical serialization. These schema constraints validate actual results; loop ceilings and separate boolean claims establish no cardinality guarantee. A required scalar can express a single selected identity directly. Object `field` bindings never provide array indexing or length access.

Explicit `EditablePaths` revisions may select an existing transform’s `/mode`, `/each` and `/resultType` alongside dependent bindings. The typed patch preserves unrelated tasks, objectives, ordering and cleanup, invalidates approval, and retains cumulative accounting. Automatic repair does not acquire this authority. Invalid immutable required bindings stop with `REVISION_REQUIRED` when no issued producer/export edit can resolve them.

## Executable tasks versus workflow documents

The planner returns business tasks; GnOuGo compiles and saves their YAML. A request
to build an agent that performs work must describe that work as actual operations,
not return another workflow's source as a successful business result. Generate an
artifact only when the artifact itself is requested. Missing executable capabilities
remain a limitation, never authority to invent operation names.

Runtime input values use typed bindings, for example
`{"kind":"input","source":"greeting"}`. The scoped regression replaces an invalid
literal `${greeting}` with that binding and verifies exact Unicode, whitespace and
literal `${...}` in the caller's data without evaluating the data a second time.
Do not escape or strip an invalid TaskPlan literal to bypass validation. Handwritten
YAML expressions and existing artifact-producing operations keep their semantics.

## Bounded automatic correction

Agent.Server enables `Request.Options.automatic_correction` only on newly created
sessions; standalone and historical sessions without the flag keep their existing
behavior. The existing loop routes compiler diagnostics to ordinary repair slots,
a separately authorized targeted revision, or one structural regeneration. No
issued slot is broadened. The original baseline, requirements and public contracts
remain authoritative. Private bounded progress in session options records candidate
and diagnostic fingerprints, action, scope/revision/hash and result. Corrections
consume the existing replan/call/token/time allowances; explicit user revisions
may replace the baseline but never reset usage.

Initial discovery with automatic correction reserves one proposal call, one repair
when available, and one clarification until answers have been recorded. At the
default eight-call ceiling, this allows five discovery calls before clarification;
answering releases that reserve but adds no allowance. The request budget exposes
`reservedCalls`. Early proposals need not consume the reserve, repeated clarification
may still exhaust the allowance, and missing contracts cause an explicit stop.
Sessions without the policy retain their existing discovery allocation. Pending
requests are always processed against their issued schema, not a recomputed reserve.

`TASK_LITERAL_INVALID` can use an existing diagnosed editable value slot. This adds
no permission over protected assertions, host-owned arguments or unrelated work.
Patches remain atomic, fully validated and subject to existing stagnation/fallback
limits. An ordinary patch cannot add operations or replace accepted requirements. A document-generation requirement cannot silently become execution; changing that intent requires an explicit revision. Intent-version 3 structural correction may add only a missing operation already required by a captured commitment.

Before any requirements or TaskPlan have been accepted, an incomplete initial
proposal can use that same single regeneration: missing/invalid requirements or
their required caller/output interfaces are diagnosed explicitly. Discovery may
leave requirements null; a clarification returns questions without a plan. A
first plan must declare business requirements and interfaces. A second invalid
proposal stops, even when the remaining allowance is larger. No requirements are
inferred from the rejected plan, and accepted intent is never replaced through
this initial-proposal path. Already-issued requests and stopped sessions remain
unchanged.

Regeneration preserves existing external operation identities, arguments, order,
control boundaries and cleanup. Independent tasks stay fixed. Assertion corrections
may replace diagnosed invalid references to declared fields on the same producer,
but preserve other operands and ordering; new assertions may follow existing ones.
New external work not captured by an accepted commitment, or changes whose preservation cannot be established, are rejected for explicit review. This conservative comparison does not prove arbitrary business
semantics or repair every structurally incomplete plan.

Intent-version 2 private correction progress retains version 3. A diagnosed unaccepted public
export may be removed without preserving the rejected proposal's extra interface.
Only names recorded from `REQUIREMENTS_OUTPUTS_CHANGED` before request issuance
and absent from accepted requirements receive this authority. Remaining export
names, order and independent bindings stay fixed; operation arguments, including
instructions, remain verbatim. This uses the existing structural revision because
binding patches cannot remove export declarations. It grants no new patch slot.
Complete output validation still distinguishes optional omission from explicit
null and checks the authoritative types. Versions 1–2 and pending receipts keep
their original preservation rules; no stopped session is automatically reopened.

A compiler-confirmed
`TASK_CONDITION_TYPE` at a `requires` slot permits a known nullable nonboolean
producer reference `value` to become `value != null`, optionally conjoined with
declared nonnullable Boolean fields of that same producer. For example,
`ready AND payload` becomes `ready AND (payload != null)` when only `payload`
is the diagnosed operand. Valid surrounding predicates, their order and the
original reference remain intact. Unknown/ambiguous types, literal success,
new disjunctions, completion checks, unrelated producers and weakened assertions
do not acquire this authority. Unsupported guard edits stop explicitly instead
of spending a structural fallback on the same unpreservable assertion.
The new guidance is included in the actual targeted patch request, not only
structural-revision context. Independent declaration changes report the changed
field, including objectives, which influence extraction and interpretation.
Historical progress and already-issued requests retain their original handling;
restart never upgrades pending authority. Complete compilation and separate
artifact review still follow preservation validation. See the
[nullable-guard replay and local execution evidence](../../docs/evidence/nullable-guard-correction-2026-10-15/README.md).

Fresh failure drafts receive sanitized machine diagnostics in `executionFailure`.
Compiler source maps identify the responsible TaskPlan location where available;
ambiguous locations do not grant a guessed targeted scope. Configuration, permission,
resource, admission, transport and unknown-completion failures stop. A valid plan
still requires requirement acknowledgments and exact artifact approval. Internal
mapping repair, compiler v15, handwritten YAML and runtime behavior are unchanged.

See [deterministic correction evidence](../../docs/evidence/automatic-correction-2026-10-14/README.md).


### Coverage correction authority (intent 3)

`REQUIREMENT_RESULT_DISCONNECTED` identifies an existing export binding for scoped repair. `REQUIREMENT_OPERATION_MISSING` requires structural regeneration, never a futile consumer patch. Private correction version 4 captures exact permitted additions (operation/capability/version/contract fingerprint), diagnosed locations, exclusive pure adaptation tasks and preserved task identities before issuing the request. Only the existing single structural fallback and cumulative allowances are used.

Candidates preserve existing operations, arguments, relative order, branches, assertions, finalizers, public interfaces and independent scope exports. New operation occurrences must match captured commitments; duplicate effects and unrelated new work are rejected. A diagnosed exclusive pure producer may be replaced without treating its old simulated effect as real evidence. Shared or guarded producers remain protected. Uncalled existing occurrences, denied capabilities and unavailable contracts do not authorize a new effect or a corrective call. A conservative stop is preferable to guessing placement or permissions.

Committed responses are applied under their original request/authority before fresh-request decisions. Historical progress versions are not upgraded. No execution is resumed to test a correction, no approval is automatic, and no allowance is reset. Coverage emits reference-only events on existing planning traces; it records no observations or schema copies.

See [offline coverage/correction evidence](../../docs/evidence/business-commitments-2026-10-16/README.md). Progressive generation remains deferred: these checks do not establish that another construction pipeline is necessary.
