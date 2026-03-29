namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Common DSL reference for GnOuGo.Flow YAML workflows.
/// Contains document structure, expression syntax, built-in functions,
/// and step common fields. Step-type-specific docs live in each executor's DslSnippet.
/// </summary>
public static class DslReference
{
    /// <summary>
    /// Common DSL reference: document structure, expressions, built-in functions, step common fields.
    /// Step-type-specific documentation is provided dynamically by each executor via IStepExecutor.DslSnippet.
    /// </summary>
    public const string CommonReference = """
# GnOuGo.Flow YAML DSL Reference (version 1)

## Document structure
```yaml
dsl: 1
name: my-workflow
workflows:
  main:
    inputs:
      message: { type: string, required: true }
      count: { type: number, required: false, default: 10 }
      items: { type: array, required: true }
    steps:
      - id: step1
        type: template.render
        input: { engine: mustache, template: "Hello {{name}}", data: { name: "${data.inputs.message}" }, mode: text }
    outputs:
      result:
        expr: "${data.steps.step1.text}"
        type: string
        description: The rendered result text
```

## Input type system
Inputs support rich type descriptors to document and validate the shape of data.

### Base types
`string`, `number`, `boolean`, `array`, `object`, `dictionary`, `any`

### Array with element type
```yaml
items:
  type: array
  items:
    type: string
```

### Object with typed properties
```yaml
config:
  type: object
  properties:
    host: { type: string, required: true }
    port: { type: number, default: 8080 }
    tags:
      type: array
      items: { type: string }
  required: [host]
```

### Dictionary (string keys, typed values)
```yaml
scores:
  type: dictionary
  additional_properties:
    type: object
    properties:
      value: { type: number }
      label: { type: string }
```

### Description
Any input can include `description:` for documentation:
```yaml
query:
  type: string
  required: true
  description: "The search query to execute"
```

All rich type fields are optional — omitting them means no structural validation beyond the base type check.

## Output type system
Outputs support the same rich type descriptors as inputs, enabling automatic JSON Schema generation for MCP tool exposure.

### Short form (expression only, backward compatible)
```yaml
outputs:
  result: "${data.steps.step1.text}"
```

### Long form (typed output)
```yaml
outputs:
  result:
    expr: "${data.steps.step1.text}"
    type: string
    description: The rendered result text
  summary:
    expr: "${data.steps.llm.json}"
    type: object
    description: Structured summary
    properties:
      title: { type: string }
      items: { type: array, items: { type: string } }
    required: [title, items]
```

Base types: `string`, `number`, `boolean`, `array`, `object`, `dictionary`, `any` (same as inputs).

## Expressions ${...}
Expressions are embedded in strings using `${...}` syntax. They are JavaScript expressions evaluated against a data context.

### Data access
- `data.inputs.*` — workflow input parameters
- `data.steps.<step_id>.*` — output of a previously executed step
- `data.env.*` — environment variables

### Operators
`&& || ! == != < <= > >= + - * / %`

### Built-in functions
- `exists(val)` → true if val is non-null
- `coalesce(a, b, ...)` → returns first non-null argument
- `len(val)` → length of string or array (returns 0 for null)
- `lower(s)` → lowercase string
- `upper(s)` → uppercase string
- `trim(s)` → trims whitespace from both ends
- `contains(s, sub)` → true if string s contains substring sub
- `startsWith(s, prefix)` → true if s starts with prefix
- `endsWith(s, suffix)` → true if s ends with suffix
- `replace(s, old, new)` → replaces all occurrences of old with new in s
- `toNumber(val)` → converts value to number
- `json(val)` → serializes value to JSON string
- `now()` → returns the current local date/time as an ISO-8601 string
- `base64(val)` → encodes the UTF-8 string value as Base64
- `formatDate(dateStr, fmt)` → formats a date string (default fmt: "yyyy-MM-dd"); also accepts unix ms timestamps

### WFScript (custom JavaScript functions)
Define custom functions in the `functions:` block at document or workflow level:
```yaml
functions: |
  function classify(text) {
    if (contains(lower(text), "urgent")) return "urgent";
    return "normal";
  }
```
Call them in expressions: `${functions.classify(data.inputs.message)}`

## Step common fields
Every step supports these fields:
```yaml
- id: unique_step_id       # required — unique within the workflow
  type: step_type           # required — one of the available step types
  if: "${expression}"       # optional — guard condition, step is skipped if false
  input: { ... }            # step-specific input data (supports ${...} expressions at any depth)
  output: alias_name        # optional — also expose this step's output as data.<alias_name>
  retry:                    # optional — automatic retry policy for retryable errors
    max: 3                  # max attempts
    backoff_ms: 1000        # initial backoff delay
    backoff_mult: 2.0       # backoff multiplier
    jitter_ms: 100          # random jitter added to delay
  on_error:                 # optional — error handler after retries are exhausted (or immediately for non-retryable errors)
    cases:
      - if: "${error.code == \"LLM_TIMEOUT\"}"
        action: continue    # continue | stop
        set_output: "${coalesce(error.message, \"fallback\")}"
      - action: stop
```

Retry semantics:
- `retry` is the mechanism that re-executes a step automatically.
- Retries happen only for runtime errors marked retryable by the executor/runtime.
- `on_error` is evaluated after retries are exhausted, or immediately for non-retryable errors.
- In `on_error` conditions, you can use `error.code`, `error.message`, `error.retryable`, `step.id`, and `step.type`.
- In the current runtime, `on_error` supports `continue` and `stop`.

Example — retry a transient timeout, then continue with fallback only if all retries fail:
```yaml
- id: summarize
  type: llm.call
  input:
    model: gpt-4o-mini
    prompt: "Summarize: ${json(data.inputs)}"
  retry:
    max: 3
    backoff_ms: 1000
    backoff_mult: 2
    jitter_ms: 100
  on_error:
    cases:
      - if: "${error.code == \"LLM_TIMEOUT\" || error.code == \"LLM_NETWORK\"}"
        action: continue
        set_output:
          text: "Temporary LLM failure after retries"
      - action: stop
```

Example — stop immediately on non-retryable validation/policy issues:
```yaml
on_error:
  cases:
    - if: "${error.code == \"INPUT_VALIDATION\" || error.code == \"TEMPLATE_POLICY\"}"
      action: stop
    - action: stop
```

## Output access pattern
Each step writes its output to `data.steps.<step_id>`. Subsequent steps can read it:
${data.steps.greet.text}, ${data.steps.loop.count}, etc.
""";
}
