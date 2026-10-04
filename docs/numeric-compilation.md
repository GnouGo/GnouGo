# Numeric compilation and MCP boundaries

Ordinary workflow calculations use JavaScript `Number` (IEEE-754 binary64). Exact business arithmetic belongs to the MCP that owns the operation. Monetary inference accounting is separate and retains its existing decimal implementation.

## Business formulas

TaskPlan keeps formulas structured until final YAML lowering. For example, `(amount + 2) * 3` is a `value` task output containing:

```json
{"kind":"arithmetic","text":"multiply","items":[
  {"kind":"arithmetic","text":"add","items":[{"kind":"input","source":"amount"},{"kind":"number","number":2}]},
  {"kind":"number","number":3}
]}
```

Binary operators are `add`, `subtract`, `multiply`, `divide` and `remainder`; `negate` takes one operand. Numeric, nonnullable source contracts and normal dependency/scope checks are required. No scripts, executor names, new inference phase or runtime inference are involved. The compiler generates parenthesized expressions in existing `set` steps after contract closure. Nonfinite results fail with `EVAL_ERROR` before output publication.

JavaScript rounding applies: `0.1 + 0.2` produces `0.30000000000000004`. Exact identifiers and precision-sensitive business computations must use an appropriate producer-owned contract, not workflow arithmetic. JSON Schema comparisons and `multipleOf` check serialized numeric values without a rounding tolerance or a decimal-range ceiling.

## Explicit decimal contracts

A producer can optionally declare a field as `{ "type": "number", "format": "decimal" }`. JSON Schema defines [number and integer](https://json-schema.org/understanding-json-schema/reference/numeric), not CLR decimal. This is a GnOuGo-recognized annotation for `System.Decimal` conversion, not a standard MCP type or mandatory extension. An unannotated `number` does not imply decimal. Producers whose SDK does not emit this annotation must publish it explicitly in their input/output schemas; the planner never guesses from tool or property names.

Before dispatch, marked inputs convert from numeric JSON into decimal values and remain JSON numbers on the wire. Marked successful outputs are validated against the producer contract and converted to JavaScript numbers. Nested objects, arrays, local references and unambiguous applicable schema branches are supported. Conflicting matching alternatives fail instead of guessing.

Normal representation rounding is permitted. Overflow, nonfinite values, nonzero underflow to zero and nonnumeric values fail. Inputs must satisfy the producer schema before and after decimal conversion. Outputs must satisfy it before binary64 conversion; subsequent consumers validate the rounded value against their own contracts. Allowed nulls and omitted properties are preserved. Conversion cannot restore precision already lost in JavaScript or perform business-specific rounding.

Failures identify server, operation, direction and JSON Pointer. Invalid inputs produce `INPUT_VALIDATION` before dispatch. Invalid successful outputs produce a durable `MCP_CALL_ERROR`, retain the producer payload, and follow existing failure/cleanup handling without replaying completed work. Original MCP error responses retain their error classification. Successful conversion receipts retain the original response; workflow bindings receive the converted response.

## Breaking migration

`NumericTransformExecutor`, `number.add`, `number.multiply` and `number.default` are removed. Retired steps fail validation before effects or cleanup, including nested workflows and finalizers. No aliases or automatic rewrites are provided. Revise affected YAML and obtain fresh approval; historical artifacts, receipts and evidence remain unchanged.

Replace addition/multiplication with ordinary checked `set` expressions. Replace requested null fallbacks with explicit conditions and branch outputs; a null check can establish a checked nonnullable value within its selected branch. Implicit defaults still require an authoritative schema or accepted input declaration and apply only to absence.

For hand-authored YAML, a checked calculation uses the existing primitive:

```yaml
- id: total
  type: set
  input: {value: "${(2 + 3) * 4}"}
  output_schema:
    type: object
    required: [value]
    properties: {value: {type: number}}
```

Generated formulas additionally check each operand with `Number.isFinite`, preventing an incorrectly reported producer value from being coerced from a string, boolean or null. Division by zero and other nonfinite results fail before downstream execution.

`TaskValue.Number`, `PlanningValue.Number` and numeric type-descriptor minima now expose `double?` in C#. Saved TaskPlan/PlanningGraph numeric JSON tokens are retained on reading to preserve historical hashes. New literal assignments use finite double values. Use the shipped `PlanningJsonContext` for source-generated plan serialization, including retention of original numeric tokens. No planning storage-version change or saved-run replay is performed. Python implementations are outside this C# migration.

## Validation

Run `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror`. Focused suites cover business formulas, real Flow execution, exact schema constraints, declared decimal conversions through actual MCP transport, non-dispatch on invalid inputs, durable failed-output receipts, restart without duplicate execution and retired-step preflight.

Run affected Release package checks, the planning Native AOT smoke and the published Agent.Server encrypted-persistence smoke. Historical paid benchmark results do not validate this change; no paid calls are required.

Reproduce the publication checks (replace `osx-arm64` with the supported host RID):

```sh
for component in Core Planning Integrations Copilot Persistence; do
  dotnet pack "src/GnOuGo.Flow.$component" -c Release -m:1 -warnaserror || exit $?
done
dotnet publish tests/GnOuGo.Flow.Planning.Smoke -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror -o /tmp/gnougo-numeric-planning-aot
/tmp/gnougo-numeric-planning-aot/GnOuGo.Flow.Planning.Smoke
dotnet publish src/GnOuGo.Agent.Server -c Release -r osx-arm64 --self-contained true -m:1 -warnaserror -o /tmp/gnougo-numeric-server-published -p:SkipClientBuild=true -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true -p:SkipModelMetadataGeneration=true -p:UseAppHost=true -p:DebugType=None -p:DebugSymbols=false
/tmp/gnougo-numeric-server-published/GnOuGo.Agent.Server --planning-persistence-smoke /tmp/gnougo-numeric-persistence-smoke
```

Use a fresh persistence-smoke directory. Native AOT checks include a real local stdio MCP decimal exchange, historical numeric-token serialization, the eight-case corpus with independently asserted execution results, approval and recovery. The existing restricted-mapping profile and historical evidence remain unchanged.

## Measured validation, 2026-10-04

Relative to `6094f415351b88346b1a9100c0ad03329de45723`, production C# changes total **390 added / 513 removed lines: 123 fewer lines**, with one executor removed and none added. Removed code includes the obsolete numeric executor and duplicate, unused instance-validation helpers. Active arithmetic fixtures now use formulas and explicit nullable branches; their independent oracles are unchanged.

On macOS arm64 with the repository .NET SDK:

| Check | Result |
| --- | --- |
| Full solution, `-m:1 -warnaserror` | 4,390 passed, 0 failed, 12 expected skips |
| Included planner / runtime / MCP integration / server suites | 899 / 1,088 / 96 / 635 passed |
| Release packages | Core, Planning, Integrations, Copilot and Persistence passed |
| Native AOT | Planning, historical token serialization and real stdio MCP decimal transport passed |
| Published trimmed server | Encrypted format-10 planning and schema-9 execution recovery passed |
| Frontend / skill | Production builds and planning-skill validation passed |

The 12 skips are seven Windows-only Cmd cases and five opt-in provider E2E cases. Windows publication and remote CI are separate from these local checks. All eight retained deterministic corpus scenarios pass their execution assertions with **one planning call and zero repairs**; arithmetic requires no runtime inference. Large/small unannotated numbers, decimal rounding/range failures, root/array schema references, original receipt retention and nonretryable failure recovery have dedicated regressions.

No paid calls, saved-workflow migration or historical benchmark changes were performed. These deterministic checks do not satisfy the outstanding Amazon/code-review live gates; PR #117 remains draft.
