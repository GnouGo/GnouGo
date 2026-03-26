# GnOuGo.Flow — YAML Workflow DSL Engine

Declarative workflow engine based on a YAML DSL, **NativeAOT**-compatible (.NET 10).

## Architecture

```
src/
  GnOuGo.Flow.Core/          # Core library
    Models/               # DSL model (Document, Workflow, Step, etc.)
    Parsing/              # Parse YAML → model (YamlDotNet RepresentationModel)
    Expressions/          # Expression engine ${...} (Jint-based evaluator)
    Templating/           # Minimal AOT-friendly Mustache engine
    Scripting/            # Jint (JavaScript) sandbox for WFScript
    Compilation/          # Document validation + compilation
    Runtime/              # Execution engine + executor registry
      Executors/          # One executor per step type
  GnOuGo.Flow.Cli/           # CLI (validate, run, inspect)
    examples/             # YAML examples
```

## YAML DSL — Quick Reference

```yaml
dsl: 1
name: my-workflow
functions: |                    # Global WFScript (JavaScript via Jint)
  function classify(text) {
    if (contains(lower(text), "urgent")) { return "urgent"; }
    return "normal";
  }

workflows:
  main:
    inputs:
      message: { type: string, required: true }
    steps:
      - id: step1
        type: template.render
        if: "${len(data.inputs.message) > 0}"
        input:
          engine: mustache
          template: "Hello {{name}}"
          data: { name: "${data.inputs.message}" }
          mode: text
        retry: { max: 3, backoff_ms: 1000 }
    outputs:
      result:
        expr: "${data.steps.step1.text}"
        type: string
        description: The rendered greeting
```

## Supported Step Types

| Type | Description |
|------|-------------|
| `sequence` | Executes sub-steps sequentially |
| `parallel` | Executes branches in parallel |
| `loop.sequential` | While/times loop |
| `loop.parallel` | Loop over items in parallel |
| `switch` | Conditional branching (form A: expr/value, form B: when) |
| `set` | Initialize/modify variables with expressions |
| `template.render` | Mustache rendering |
| `llm.call` | LLM call (structured output supported) |
| `mcp.call` | Call MCP server tools/prompts/resources |
| `mcp.list` | List capabilities of an MCP server |
| `emit` | Emit a thinking/progress/info message to the telemetry stream |
| `human.input` | Pause the workflow and wait for human input |
| `workflow.call` | Call a local or remote sub-workflow |
| `workflow.plan` | Generate a workflow dynamically via LLM |
| `workflow.execute` | Execute a planned workflow |

## Typed Inputs

Workflow inputs support rich type declarations with validation at runtime.

**Supported types:** `string`, `number`, `boolean`, `array`, `object`, `dictionary`, `any`

```yaml
workflows:
  main:
    inputs:
      # Simple scalar
      name:
        type: string
        required: true
        description: The user's name

      # With default value
      mode:
        type: string
        required: false
        default: standard
        description: Processing mode

      # Array with typed items
      tags:
        type: array
        items: { type: string }
        required: false
        default: []

      # Nested object
      config:
        type: object
        properties:
          timeout: { type: number }
          retries: { type: number }
        required: false

      # Dictionary (string keys, typed values)
      headers:
        type: dictionary
        additionalProperties: { type: string }
```

## Typed Outputs

Workflow outputs support type annotations and descriptions. This enables:
- Self-documenting workflow contracts
- Automatic JSON Schema generation (for MCP tool exposure)
- Nested type descriptors for arrays, objects, and dictionaries

### Short form (expression only)

```yaml
    outputs:
      result: "${data.steps.step1.text}"
```

### Long form (with type and description)

```yaml
    outputs:
      summary:
        expr: "${data.steps.llm_summary.text}"
        type: string
        description: LLM-generated summary text

      items_processed:
        expr: "${data.steps.process.count}"
        type: number
        description: Number of items processed

      success:
        expr: "${data.steps.result.ok}"
        type: boolean
        description: Whether the workflow succeeded
```

### Complex types

```yaml
    outputs:
      # Array of strings
      tags:
        expr: "${data.steps.extract.tags}"
        type: array
        items:
          type: string
        description: Extracted tags

      # Typed object
      report:
        expr: "${data.steps.build.report}"
        type: object
        properties:
          title:
            expr: "${data.steps.build.report.title}"
            type: string
          score:
            expr: "${data.steps.build.report.score}"
            type: number
        description: Structured report

      # Dictionary
      metrics:
        expr: "${data.steps.collect.metrics}"
        type: dictionary
        additionalProperties:
          type: number
        description: Named metrics map
```

### JSON Schema generation

`OutputDef` types are convertible to JSON Schema via `JsonSchemaConverter.OutputsToJsonSchema(outputs)`, used for MCP tool exposure and API documentation.

## `${...}` Expressions

- Access: `data.inputs.*`, `data.steps.<id>.*`, `data.env.*`
- Optional chaining: `data.steps.maybe_skipped?.value`
- Operators: `&& || ! == != < <= > >= + - * / ??`
- Built-in functions: `exists`, `coalesce`, `len`, `lower`, `upper`, `trim`, `contains`, `startsWith`, `endsWith`, `replace`, `toNumber`, `json`, `formatDate`
- WFScript functions: `functions.myFn(...)` → calls a function defined in `functions:`
- Full JavaScript: ternary (`a ? b : c`), template literals (`` `Hello ${data.inputs.name}` ``), array methods

## CLI

```bash
# Validate a workflow
dotnet run --project src/GnOuGo.Flow.Cli -- validate examples/triage.yaml

# Inspect the structure
dotnet run --project src/GnOuGo.Flow.Cli -- inspect examples/triage.yaml

# Execute a workflow
dotnet run --project src/GnOuGo.Flow.Cli -- run examples/triage.yaml -i 'message=hello' -i 'priority=normal'

# With full JSON
dotnet run --project src/GnOuGo.Flow.Cli -- run examples/triage.yaml -j '{"message":"hello","priority":"normal"}'
```

## Error Handling

Each step supports `retry` and `on_error`:

```yaml
retry: { max: 3, backoff_ms: 1000, backoff_mult: 2.0, jitter_ms: 100 }
on_error:
  cases:
    - if: "${error.code == \"LLM_TIMEOUT\"}"
      action: retry
    - action: continue
      set_output: "${coalesce(error.message, \"fallback\")}"
    - action: stop
```

## NativeAOT

- `GnOuGo.Flow.Core`: `IsAotCompatible=true`
- `GnOuGo.Flow.Cli`: `PublishAot=true`
- YAML: YamlDotNet RepresentationModel (DOM, no reflection)
- JSON: `System.Text.Json.Nodes.JsonNode` everywhere (no reflection-based serialization)
- Templating: Manually implemented Mustache (no external library)
- Scripting: Jint v4+ (pure interpreter, no Reflection.Emit)
