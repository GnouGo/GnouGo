# GnOuGo.Flow.Planning

Provider-neutral incremental TaskPlan construction, deterministic semantic validation,
scoped typed repair and JavaScript → YAML v15 compilation. This package does not execute
business effects or authorize production permissions.

## Planning and review

Internal planning format **11** has one active protocol. Retired sessions and approvals
are rejected without migration; original encrypted records remain unchanged. TaskPlan
and execution journal schema 9 are separate from this storage rupture.

Each generation request addresses immediate work using original requirements, accepted
producers and selected original contracts. Compiler preflight validates exact technical
bindings before acceptance. Business review is decoupled from fragment generation:
coherent blocks can contain several checkpoints; mechanical openings/exports/closures
need no Judge call. A completed small workflow may receive one global review and no
local reviews. Every complete artifact requires global business review before approval.

The Judge receives only faithful decision-relevant behavior and original business
semantics. Full contract schemas remain host-validated and retrievable. The response is
one strict `ok`, `needs_change` or `needs_context` decision. Program-owned receipts,
hashes and authority do not enter the response contract. Context acquisition preserves
the separate bounded chance to correct an interpretation on unchanged evidence.

Original requirements and permissions survive repairs. Typed patches edit only issued
slots; cosmetic novelty does not resolve a defect. Preserve executable literals,
transform objectives, dependencies and cleanup. Scoped revision of accepted work
invalidates approval and retains independent checkpoints and cumulative accounting.

See [incremental construction](../../docs/incremental-taskplan-construction.md),
[package architecture](../../docs/workflow-planning-v9.md), and
[benchmark operation](../../tests/GnOuGo.Agent.Planning.Benchmark/README.md).

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

### Explicit consumer views and flatten

`flatten` is an additive TaskValue/PlanningValue binding with exactly one operand in `items`. It accepts typed, nonnullable arrays of arrays and concatenates one level in source order. Element nulls, duplicates, deeper arrays and exact references survive; null inner arrays and opaque elements fail. Compiler-owned checked selections enforce source constraints before concatenation and consumer constraints afterward. Extraction result contracts and explicit object/field bindings define closed consumer inputs. Global interpretation still has to fit complete-request admission; flattening a broad result does not compact its fields.

### ID-only selection and lookup

For decision-driven actions, declare the extract `resultType` as candidate identity plus facts needed by that consumer. Bind only this view to interpretation. Keep complete original observations and authoritative action arguments separately. The compiler never infers relevance or drops fields. A necessary oversized view still fails request admission.

`lookup` is a closed TaskValue binding: `kind: lookup`, literal `port: identityField`, and `items: [recordCollection, selectedIds]`. It returns original records in selected-ID order, including repeated selections. Missing/null/blank keys, ambiguous duplicate source identities, unknown selections and incompatible types fail with `CONTRACT_UNSATISFIED` before output publication. String comparison is ordinal; numeric identities must be safe integers, without string coercion. Empty selection remains empty, but does not excuse invalid source identities. Source and downstream schemas remain checked.

Use it only to reconnect selection: verify selected IDs against the offered candidate view, project those verified IDs, then resolve them against retained original records. Producer IDs are preferred; otherwise use the original collection index attached before extraction/filtering. Index identities belong to that captured collection, not a later snapshot. Never hash content or renumber surviving candidates. No model-generated action argument is used on this path; producer permissions, action compatibility and expiration still apply.

### Array cardinality and explicit transform revisions

Explicit `EditablePaths` revisions may select an existing transform’s `/mode`, `/each` and `/resultType` alongside dependent bindings. The typed patch preserves unrelated tasks, objectives, ordering and cleanup, invalidates approval, and retains cumulative accounting. Automatic repair does not acquire this authority. Invalid immutable required bindings stop with `REVISION_REQUIRED` when no issued producer/export edit can resolve them.

## Executable tasks versus workflow documents

Model instructions describe business tasks and typed bindings, without exposing
compiler serialization details. Plan approval belongs to the host, not a generated
business task; explicitly requested human business decisions remain supported. A request
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



## Validation and publishing

Run affected tests with warnings as errors, then the shared solution:

```sh
dotnet test tests/GnOuGo.Flow.Planning.Tests -m:1 -warnaserror
dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
dotnet pack src/GnOuGo.Flow.Planning -c Release -warnaserror
```

Preserve original-contract, permission, provenance, rollback, tenant-isolation and v15
regressions. Validate Native AOT consumers and published encrypted-storage recovery.
Source-bound technical passes do not establish independent runtime business success.
