# Typed business-field bindings

## Confirmed failure

Session `79328cefb7d54d808fa132382f3aee70` failed while binding a typed collection of
review records to scalar operation arguments. Read-only diagnosis at `fe82fb9`
replayed its original TaskPlan and both repairs through `HybridWorkflowPlanner`:

1. `output(source="item", port=...)` referenced a nonexistent task instead of the
   loop variable: five `TASK_REFERENCE_UNKNOWN` diagnostics.
2. The first repair inserted a transform and changed unrelated objectives and an
   input contract: `REVISION_SCOPE_CHANGED`, with the accepted baseline retained.
3. The second repair bound the whole `item` object to `body`, `path`, `side`,
   `startLine` and `line`: five `TASK_INPUT_TYPE` diagnostics. The session stopped
   after five model calls and two repairs.

The existing strict operation contracts were correct. TaskPlan lacked a way to
select a field of a record. Its transform's nested `side` declaration also lacked
the operation's explicit `LEFT`/`RIGHT` enum. Increasing budgets or using another
transform to copy fields does not fix the missing deterministic binding.

## Generic correction

A value such as `{"kind":"field","items":[{"kind":"item"}],"port":"body"}`
selects one declared field of one typed business object. The same contract works
for inputs, outputs and nested selections. Names remain literal, including dots
or punctuation; no wire path, expression or operation-name heuristic is accepted.

Compiler preflight validates every selection against authoritative types. Lowering
composes nested selections into one existing `value.project` check at consumption.
Missing fields fail, nullable fields remain nullable, and opaque objects cannot
supply fields. No inference, implicit cast, fallback value or task insertion is
added. Producer type locations survive iteration/captures, so existing scoped
repair can diagnose the precise nested enum without permitting unrelated edits.

The response schema and compact repair serialization expose the additive value;
public DTO fields, planning format 10 and execution journal schema 9 are unchanged.
Existing approval verification recompiles and detects changed field selections.
No runtime, persistence, provider adapter or permission implementation changed.

## Deterministic evidence

The host regression retains all five responses, original request schemas and
actual discovered contracts. A separate synthetic fixture changes only the five
bindings and the nested enum; it reaches final review with the two recorded
discovery responses and one synthetic TaskPlan response. This is **not** a measured
live planning result.

The real workflow engine executes that YAML with mocked external operations and
inference. Independent assertions verify three different records' exact scalar
arguments, `endLine` → `line`, order and cleanup. Four retained semantic transforms
remain four inference calls: field selection adds none. Coverage includes empty
records, missing/null/incorrect fields, invalid enums, comment failure, cancellation,
absent producers and permission refusal. Unit tests cover nested selections and
iterations, reusable groups, conditional availability, literal names, scoped repair,
renamed operations/distractors, serialization and approval invalidation. Native AOT
smoke includes typed loop-record selection without an inference client.

Local validation: `dotnet test GnOuGo.Agent.sln --no-restore -m:1 -warnaserror -p:SkipClientBuild=true` completed with **3,184 passed, zero failed and five opt-in live skips** across 33 test projects. This includes **354 planner tests** and the **12 recorded review planning/execution cases**. The macOS ARM64 Native AOT publish and smoke passed, including all eight existing business scenarios and the new field-selection case. The five independently publishable Flow packages passed `dotnet pack -c Release -warnaserror`. Current guidance links, skill frontmatter and `git diff --check` were verified. Branch CI results and its exact revision are reported on draft PR #113; local validation does not substitute for those jobs.

## Operational recovery and limitations

Deploy the updated planner, then generate and explicitly approve a new workflow.
The original session's exhausted repair budget, encrypted records, requests and
accounting remain unchanged. Pending historical responses retain their original
schemas; this fix does not silently repair saved plans or replenish budgets.

No paid inference, live benchmark, GitHub mutation or real Copilot task was run.
The retained workflow's broader review choices are not certified by this binding
regression. Real Copilot sandbox command execution remains unverified; do not
bypass its restrictions. The PR remains draft and must not be merged automatically.
