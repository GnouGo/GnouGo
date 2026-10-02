# .NET Flow runtime primitive consolidation

This change continues PR #117. It preserves `LLM → business TaskPlan → deterministic compiler → PlanningGraph → YAML`, public plan formats and storage format 10. There are no new planning phases, service interfaces, executors or MCP-specific rules.

## Audit and implementation

| Previous executor | Real usage | Current implementation |
| --- | --- | --- |
| `NumericTransformExecutor` | Advertised `number.add`, `number.multiply`, `number.default`; four arithmetic benchmark scenarios | Retained. Checked decimal arithmetic is not equivalent to JavaScript double arithmetic; TaskPlan does not expose arbitrary expressions. |
| `DecisionEvaluateExecutor` | Public DSL, finite selector validation and tests; not emitted by TaskPlan compilation | Removed. Check conditions and finite results with schema-checked `set`, then route with `switch`. |
| `AssertNonNullExecutor` | Compiler confirmation gate, nullable-field refinement and finalization tests | Removed. The gate uses strict boolean computation and `enum: [true]`; consumed data uses explicit required/nonnullable schemas. |
| `ArrayProjectExecutor` | Foreach output collection, checked selectors and artifact provenance | Removed. `value.project` with `each: true` selects each item's first present path. |
| `ValidateValueExecutor` | Foreach bounds and opaque whole-value contracts; published JSON-text mode | Removed. `value.project` with `paths: [[]]` checks the whole value; parsing remains separate. |
| `ValueProjectExecutor` | Conditional merges, field projection, whole-value validation | Retained and consolidated, with unchanged default behavior. |
| `AgentRunExecutor` | Bounded execution, evidence verification and reconciliation | Unchanged. |

Four classes are removed (23 → 19). Their files contained 339 lines before consolidation. The production C# diff is **117 added / 498 removed: 381 net fewer lines**, including registrations, validation and lowering changes. `AgentRunExecutor` and `NumericTransformExecutor` are byte-for-byte unchanged.

The compiler emits literal projection paths and modes. The model continues to select business fields and collection outputs. No projection recipes or JavaScript are added to TaskPlan generation.

## Checked projection

`value.project` requires `value`, a nonempty list of candidate `paths`, and a literal `output_schema` describing `{value: ...}`. Optional `each` is boolean and defaults to false; explicit null is invalid. With `each: true`, the source must be an array. Selection runs independently for each item, producing `{value: [...]}`.

Missing paths try the next candidate. Explicit null is present, and an invalid present value fails without trying another candidate. Empty paths select whole values. Selection preserves order, duplicates and nested arrays; it neither drops records nor flattens collections. Nothing is published until the complete result passes validation. Cancellation is observed during iteration.

For foreach, the compiler first checks the original item contract and `maxItems` using whole-value projection, then collects the loop's declared outputs with per-item projection. Runtime bounds, missing-field errors, cleanup guards and sequential/parallel ordering remain enforced.

Opaque producers gain typed access only after explicit whole-value checking. A schema establishes a runtime shape, never artifact ownership or provenance. The provenance validator follows actual identity-preserving inputs; every reachable selection alternative must retain the required origin. Optional selectors, unchecked array indexing and unsafe continuations remain insufficient evidence.

## Migration examples

### Checked decisions

A switch selects the first match. Check exclusivity separately when the business decision requires it. This example permits zero matches as `NONE`, rejects overlap and checks both conditions before computing anything:

```yaml
- id: conditions
  type: set
  input: {first: '${data.inputs.first}', second: '${data.inputs.second}'}
  output_schema:
    type: object
    required: [first, second]
    properties: {first: {type: boolean}, second: {type: boolean}}
- id: decision
  type: set
  input:
    exclusive: '${!(data.steps.conditions.first && data.steps.conditions.second)}'
    outcome: '${data.steps.conditions.first ? "FIRST" : data.steps.conditions.second ? "SECOND" : "NONE"}'
  output_schema:
    type: object
    required: [exclusive, outcome]
    properties:
      exclusive: {type: boolean, enum: [true]}
      outcome: {type: string, enum: [FIRST, SECOND, NONE]}
```

For exactly one match, use `first !== second` for `exclusive`. Route on the checked `outcome` with an ordinary `switch`. Multiple computed fields in one checked set are published atomically. No generic decision interpreter replaces the retired executor.

### Required fields and whole-value checks

```yaml
- id: checked
  type: value.project
  input:
    value: '${data.inputs.payload}'
    paths: [[]]
  output_schema:
    type: object
    required: [value]
    properties:
      value:
        type: object
        required: [identifier]
        properties: {identifier: {type: string}}
```

Consume `data.steps.checked.value.identifier`. Declare nested required fields and nonnullable item types when needed. This is an explicit contract, not an implicit recursive “no null anywhere” guarantee for unconstrained JSON.

### Collection projection

Replace `array.project` input `items`/`path` with `value.project` input `value`/`paths`/`each: true`. Rename the output envelope field `values` to `value` in its schema and consumers. For example, `path: [row]` becomes `paths: [[row]]`; no extra loop is needed.

### JSON text

Parse strictly in an ordinary set, then check the whole parsed value:

```yaml
- id: parse
  type: set
  input:
    value: "${((text) => { if (typeof text !== 'string') throw new Error('Expected JSON text'); return JSON.parse(text); })(data.inputs.text)}"
- id: checked
  type: value.project
  input: {value: '${data.steps.parse.value}', paths: [[]]}
  output_schema:
    type: object
    required: [value]
    properties:
      value:
        type: [object, 'null']
        required: [x]
        properties: {x: {type: integer}}
```

Malformed text and non-string inputs fail even when the target permits null. Do not replace strict parsing with `fromJson`, which returns null on malformed text. JavaScript numbers have double precision: this migration is not a lossless replacement for arbitrary decimal JSON numbers or integers beyond its exact range. Preserve exact identifiers/decimals as strings or use an authoritative producer contract; the existing numeric executors retain their decimal semantics.

## Compatibility and deployment

This is a breaking .NET DSL/API cleanup. The four executor classes and their registered contracts are removed, along with the unused `FlowTypeDescriptor.RemoveNullDeep` helper and `ErrorCodes.DecisionEvaluationUnresolved` constant. Historical stored error strings remain readable. `WorkflowValidator` and compilation report `STEP_TYPE_RETIRED`; execution and resume also reject retired steps in already compiled documents before effects, cleanup or journal changes. Unrelated custom executor registration remains supported.

Stored YAML, approvals, history and execution journals are not rewritten. Revise affected workflows explicitly, refresh discovery, validate and approve a new artifact. After upgrading, start a fresh planning session with the business plan as a revision baseline rather than continuing a proposal against frozen native contracts. Do not inject changed definitions into old runs or automatically replay them. Existing contract fingerprints invalidate affected contracts normally; historical fingerprints and receipts remain intact.

Python runtime implementations are outside this change. Shared historical fixtures retain the previous primitive names for evidence; .NET rejects those artifacts until explicitly revised. Historical benchmark results, including 33/33, are unchanged and do not validate this cleanup.

## Validation

Baseline: 177 focused existing tests passed with `-warnaserror` before editing. New regressions cover checked projection, strict parsing, decision compositions, retired steps in all control-flow positions, unchanged durable journals and custom executors. Compiler regressions cover strict confirmation, collection bounds and literal projection configuration. Existing local product execution independently inspects generated XLSX cells.

All execution evidence for this change is deterministic and local. No paid inference, real marketplace browsing, external repository execution or saved-workflow rerun is included.

Final local validation of implementation commit `542195149462a1533d4bb1b25ec7cbf251ef98d4` (macOS ARM64, .NET 10.0.300):

- Full solution: **4,082 passed, zero failures, 12 skips**, across 33 test projects with `-warnaserror`. Skips are seven Windows-only Cmd cases and five opt-in Copilot end-to-end cases.
- Included suites: **983 Flow runtime, 783 planning, 593 Agent.Server and 12 Mermaid tests**. Browser clarification is enabled in the final solution run and also passed separately.
- Six four-task business product variants execute real Flow with actual Document writing; seven local-site variants additionally exercise Browser/Document stdio. Independent XLSX, empty/missing collection, permission-denial and cleanup assertions pass. These are deterministic local executions, not live-provider evidence.
- The eight original Native AOT corpus scenarios pass with one scripted planning call and zero repairs each, including unchanged decimal arithmetic oracles. Typed collections, conditional branches, cleanup and repair recovery also pass in the published binary. No provider token/cost or general latency improvement is claimed.
- Release packs pass for Core, Planning, Integrations, Copilot, Persistence and Mermaid. Planning Native AOT publish/execution and the frontend production build pass without new warning suppressions.

The first full run retained two failures: an offline revised proposal reused an old native projection contract, and an existing Copilot cancellation test raced temporary-directory cleanup. New offline revisions now refresh native contracts while preserving original recordings and pending-request tests. The final complete run passed without changing Copilot production or test code; that unrelated cleanup race remains a known intermittent limitation.

Reproduce from the repository root; the browser module points to an installed Playwright package:

```sh
PLAYWRIGHT_MODULE_PATH=/absolute/path/to/playwright/index.mjs dotnet test GnOuGo.Agent.sln -m:1 -warnaserror
for component in Core Planning Integrations Copilot Persistence Mermaid; do
  dotnet pack "src/GnOuGo.Flow.$component/GnOuGo.Flow.$component.csproj" -c Release -warnaserror
done
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -warnaserror -o /tmp/flow-primitives-smoke
/tmp/flow-primitives-smoke/GnOuGo.Flow.Planning.Smoke
```

Use the supported RID for the target platform. CI additionally exercises Linux builds and published contracts; its current results are attached to PR #117. Historical evidence files and shared benchmark inputs are unchanged.
