# GnOuGo.Flow — YAML Workflow DSL Engine

Declarative workflow engine based on a YAML DSL, **NativeAOT**-compatible (.NET 10).

## Architecture

```
src/
  GnOuGo.Flow.Core/          # Core library
    Models/               # DSL model (Document, Workflow, Step, etc.)
    Parsing/              # Parse YAML → model (YamlDotNet RepresentationModel)
    Expressions/          # Expression engine ${...} (lexer, parser, evaluator)
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
      result: "${data.steps.step1.text}"
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
| `llm.call` | LLM call |
| `workflow.call` | Call a local or remote sub-workflow |
| `workflow.plan` | Generate a workflow via LLM |
| `workflow.execute` | Execute a planned workflow |

## `${...}` Expressions

- Access: `data.inputs.*`, `data.steps.<id>.*`, `data.env.*`
- Operators: `&& || ! == != < <= > >= + - * /`
- Built-in functions: `exists`, `coalesce`, `len`, `lower`, `upper`, `trim`, `contains`, `startsWith`, `endsWith`, `replace`, `toNumber`, `json`, `formatDate`
- WFScript functions: `functions.myFn(...)` → calls a function defined in `functions:`

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
      set_output: "${coalesce(error.message, \"fallback\")}
    - action: stop
```

## NativeAOT

- `GnOuGo.Flow.Core`: `IsAotCompatible=true`
- `GnOuGo.Flow.Cli`: `PublishAot=true`
- YAML: YamlDotNet RepresentationModel (DOM, no reflection)
- JSON: `System.Text.Json.Nodes.JsonNode` everywhere (no reflection-based serialization)
- Templating: Manually implemented Mustache (no external library)
- Scripting: Jint v4+ (pure interpreter, no Reflection.Emit)
