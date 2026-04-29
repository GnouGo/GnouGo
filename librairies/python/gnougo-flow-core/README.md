# gnougo-flow-core — YAML Workflow DSL Engine (Python)

[![PyPI](https://img.shields.io/pypi/v/gnougo-flow-core?label=PyPI)](https://pypi.org/project/gnougo-flow-core/)

Python 3.10+ implementation of `GnOuGo.Flow.Core`, the declarative YAML workflow DSL engine.
Write YAML workflows that orchestrate LLMs, MCP servers, templates, loops, human input, and dynamic code generation — all from a single file.

---

## Package Status and Parity

The .NET library at [`src/GnOuGo.Flow.Core/`](../../../src/GnOuGo.Flow.Core/) is the **source of truth**.
This Python package mirrors its public surface as closely as Python idioms allow. See [`PORTING_TODO.md`](PORTING_TODO.md) for the detailed parity log and remaining work items.

| Area | Status |
|---|---|
| YAML DSL parser (`version:` / `dsl:` alias) | Yes |
| Validation + compilation pipeline | Yes |
| Expression interpolation `${...}` + built-in functions | Yes (AST-based JS-subset interpreter) |
| Mustache `template.render` engine | Yes |
| WFScript (`functions:` block) | Yes multi-statement (`var`/`let`/`const`, `if`/`else`, `return`) |
| Runtime engine + step registry | Yes |
| Step types: `set`, `emit`, `sequence`, `parallel`, `loop.sequential`, `loop.parallel`, `switch`, `template.render`, `llm.call`, `mcp.list`, `mcp.call`, `human.input`, `workflow.call`, `workflow.plan`, `workflow.execute` | Yes |
| MCP integrations (`InMemoryMcpClientFactory`, `ConfiguredMcpClientFactory`, cache helper) | Yes |
| `LLMRequest.reasoning` field | Yes |
| `workflow.plan` defaults `reasoning="high"` | Yes |
| Workflow source telemetry (`source_text` / `source_format`) | Yes |
| `JsonSchemaConverter` (inputs/outputs to JSON Schema) | Yes |
| `WorkflowCheckpointer` + `WorkflowEngine.resume_async` | Yes |
| CLI: `validate` / `inspect` / `run` subcommands | Yes |

---

## Table of Contents

- [Package Status and Parity](#package-status-and-parity)
- [Architecture](#architecture)
- [Quick Start](#quick-start)
- [Document Structure](#document-structure)
- [Step Types Reference](#step-types-reference)
  - [template.render](#templaterender--mustache-templating)
  - [llm.call](#llmcall--call-a-language-model)
  - [mcp.list](#mcplist--discover-mcp-server-capabilities)
  - [mcp.call](#mcpcall--call-mcp-tools-or-prompts)
  - [set](#set--initialize-or-modify-variables)
  - [emit](#emit--send-progress-messages-to-the-ui)
  - [human.input](#humaninput--pause-and-wait-for-user-input)
  - [sequence](#sequence--run-steps-sequentially)
  - [parallel](#parallel--run-branches-in-parallel)
  - [loop.sequential](#loopsequential--iterate-sequentially)
  - [loop.parallel](#loopparallel--iterate-in-parallel)
  - [switch](#switch--conditional-branching)
  - [workflow.call](#workflowcall--call-a-sub-workflow)
  - [workflow.plan](#workflowplan--generate-a-workflow-dynamically-via-llm)
  - [workflow.execute](#workflowexecute--execute-a-planned-workflow)
- [Typed Inputs](#typed-inputs)
- [Typed Outputs](#typed-outputs)
- [Expressions `${...}`](#expressions-)
- [WFScript - Custom JavaScript Functions](#wfscript--custom-javascript-functions)
- [Error Handling](#error-handling)
- [CLI](#cli)
- [Python Runtime Notes](#python-runtime-notes)

---

## Architecture

```text
librairies/python/gnougo-flow-core/
  pyproject.toml                    # Python package metadata and dependencies
  src/gnougo_flow_core/             # Publishable Python library
    models.py                       # DSL model (Document, Workflow, Step, etc.)
    parsing.py                      # Parse YAML to model (PyYAML)
    expressions.py                  # Expression interpolation `${...}`
    _jsmini.py                      # In-tree JS-subset interpreter for expressions and WFScript
    templating.py                   # Minimal Mustache-compatible renderer
    scripting.py                    # WFScript helpers
    compilation.py                  # Document validation + compilation
    runtime.py                      # Execution engine + executor registry
    runtime_contracts.py            # Protocols for LLM, MCP, HITL, workflow fetching, telemetry
    checkpointing.py                # Workflow checkpoint contracts and in-memory implementation
    integrations/                   # MCP and LLM adapter helpers
    runtime_steps/                  # Executor re-export modules for step families
  tests/                            # Dedicated Python unit tests
```

The package is intentionally independent from the .NET assembly at runtime. It keeps the same DSL concepts and stable contracts so workflows can be shared across Python and .NET hosts.

---

## Quick Start

Install the published Python package:

```bash
python -m pip install gnougo-flow-core
```

Or add it to a local `uv` project:

```bash
uv add gnougo-flow-core
```

For repository development, install the package with its development extras from this directory:

```bash
uv sync --extra dev
```

Create `hello.yaml`:

```yaml
version: 1
name: hello-world
workflows:
  main:
    inputs:
      name: { type: string, required: true }
    steps:
      - id: greet
        type: template.render
        input:
          engine: mustache
          template: "Hello {{name}}! Welcome to GnOuGo.Flow."
          data: { name: "${data.inputs.name}" }
          mode: text
    outputs:
      greeting: "${data.steps.greet.text}"
```

Validate it:

```bash
gnougo-flow validate hello.yaml
```

Inspect it:

```bash
gnougo-flow inspect hello.yaml
```

Run it from the CLI:

```bash
gnougo-flow run hello.yaml -i name=World
```

Run it from Python:

```python
import asyncio
from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine, apply_workflow_input_defaults
async def main() -> None:
    yaml_text = open("hello.yaml", encoding="utf-8").read()
    document = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(document)
    workflow = compiled.workflows[compiled.entrypoint]
    inputs = apply_workflow_input_defaults(workflow.source, {"name": "World"})
    result = await WorkflowEngine().execute_async(workflow, inputs)
    if not result.success:
        raise RuntimeError(result.error.message if result.error else "Workflow failed")
    print(result.outputs)
asyncio.run(main())
```

Runtime integrations such as LLM clients, MCP clients, human input providers, workflow fetchers, telemetry, and checkpointing are injected through Python protocols in `gnougo_flow_core.runtime_contracts`.

---

## Document Structure

Every workflow file starts with:

```yaml
version: 1                        # DSL version (required, always 1)
name: my-workflow             # Document name (optional)
functions: |                  # Global WFScript functions (optional)
  function myHelper(x) { return x * 2; }

workflows:
  main:                       # Entrypoint workflow (by convention)
    inputs:                   # Input parameters with types (optional)
      message: { type: string, required: true }
    steps:                    # Ordered list of steps (required)
      - id: step1
        type: template.render
        input: { ... }
    outputs:                  # Output expressions (optional)
      result: "${data.steps.step1.text}"
```

You can define **multiple workflows** in the same document and call them via `workflow.call`.

### Step Common Fields

Every step supports:

```yaml
- id: unique_step_id         # Required — unique within the workflow
  type: step_type             # Required — one of the step types below
  if: "${expression}"         # Optional — guard; step is skipped if false
  input: { ... }              # Step-specific input (supports ${...} at any depth)
  output: alias_name          # Optional — also expose output as data.<alias_name>
  retry:                      # Optional — automatic retry for retryable errors
    max: 3
    backoff_ms: 1000
    backoff_mult: 2.0
    jitter_ms: 100
  on_error:                   # Optional — error handler (see Error Handling)
    cases:
      - if: "${error.code == \"LLM_TIMEOUT\"}"
        action: continue
        set_output: "fallback value"
      - action: stop
```

### Data Access

All expressions read from a shared `data` context:

| Path | Content |
|------|---------|
| `data.inputs.*` | Workflow input parameters |
| `data.steps.<step_id>.*` | Output of a previously executed step |
| `data.env.*` | Environment variables |

---

## Step Types Reference

### `template.render` — Mustache Templating

Renders a Mustache template with data from the workflow context.

```yaml
- id: greet
  type: template.render
  input:
    engine: mustache
    template: "Hello {{name}}, you have {{count}} items."
    data:
      name: "${data.inputs.name}"
      count: "${len(data.inputs.items)}"
    mode: text                # "text" (default) or "json"
```

**Output:** `{ text: "Hello World, you have 3 items." }`

---

### `llm.call` — Call a Language Model

Sends a prompt to an LLM and returns the response. Supports structured JSON output.

#### Basic call

```yaml
- id: summarize
  type: llm.call
  input:
    model: gpt-4o-mini                              # Required
    prompt: "Summarize this: ${data.inputs.text}"    # Required
    system: "You are a concise summarizer."          # Optional
    provider: openai                                 # Optional (default: auto-routed)
    temperature: 0.7                                 # Optional
    max_tokens: 2048                                 # Optional
    reasoning: auto                                  # Optional — auto|minimal|low|medium|high|max
                                                     # Default: omitted (provider decides).
                                                     # Models without thinking support ignore it.
```

**Output:** `{ text: "...", usage: { prompt_tokens, completion_tokens, total_tokens }, meta: { model } }`

#### Structured output (JSON mode)

```yaml
- id: classify
  type: llm.call
  input:
    model: gpt-4o
    prompt: "Classify this ticket and return JSON: ${data.inputs.ticket}"
    structured_output:
      schema_inline:
        type: object
        properties:
          category: { type: string }
          priority: { type: string, enum: [low, medium, high, critical] }
          confidence: { type: number }
        required: [category, priority]
      strict: true
```

**Output:** `{ text: "...", json: { category: "bug", priority: "high", confidence: 0.92 }, usage: {...} }`

Access: `data.steps.classify.json.category`, `data.steps.classify.json.priority`

---

### `mcp.list` — Discover MCP Server Capabilities

Lists tools, resources, and/or prompts exposed by one or more MCP servers.
Use a one-item array for a single server, or `servers: ["*"]` to discover all configured MCP servers.

```yaml
- id: discover
  type: mcp.list
  input:
    servers: [github, docs]         # Required — configured MCP server names
    include: ["tools", "prompts"] # Optional — default: ["tools"]

- id: discover_all
  type: mcp.list
  input:
    servers: ["*"]
    include: ["tools"]
```

**Output:** `{ status, text, servers: [...], tools: [...], resources: [...], prompts: [...] }`

Flattened `tools`, `resources`, and `prompts` entries each include a `server` field so downstream steps can keep the server affinity when multiple MCP servers are discovered at once.

---

### `mcp.call` — Call MCP Tools or Prompts

Calls one or more capabilities on an MCP server. Three modes are available:

#### Direct tool call (preferred when tool names are known)

```yaml
- id: weather
  type: mcp.call
  input:
    server: weather-server
    kind: tool
    method: get_weather
    request: { location: "Paris", units: "celsius" }
    timeout_ms: 30000
```

**Output:** `{ status: "ok", response: { temperature: 22, ... } }`

#### Direct prompt call

```yaml
- id: summarize_prompt
  type: mcp.call
  input:
    server: my-server
    kind: prompt
    method: summarize_document
    request: { text: "${data.inputs.document}" }
```

**Output:** `{ status: "ok", text: "...", messages: [...] }`

#### LLM-assisted call (auto-selects the right tool)

Combine `mcp.list` → `mcp.call` with a prompt to let an LLM choose the best tool:

```yaml
- id: discover
  type: mcp.list
  input:
    servers: [github]

- id: smart_call
  type: mcp.call
  input:
    server: github
    model: gpt-4o-mini
    temperature: 0.2
    prompt: "Find and call the right tool to list my repositories"
    tools: "${data.steps.discover.tools}"
    prompts: "${data.steps.discover.prompts}"
    structured_output:
      schema_inline:
        type: object
        properties:
          repos:
            type: array
            items:
              type: object
              properties:
                name: { type: string }
                url: { type: string }
              required: [name, url]
        required: [repos]
      strict: true
```

**Output (LLM-assisted):** `{ status: "ok", selection_mode: "llm", text: "...", tool_calls: [...], results: [...], json: {...} }`

#### Output access patterns

| Mode | Access |
|------|--------|
| Single tool | `data.steps.<id>.status`, `data.steps.<id>.response` |
| Single prompt | `data.steps.<id>.status`, `data.steps.<id>.text` |
| Batch/auto | `data.steps.<id>.results` (array) |
| LLM-assisted | `data.steps.<id>.text`, `data.steps.<id>.json` |

> **Important:** The `response` object is tool-specific. Do not assume field names unless documented by the tool. Use `json(data.steps.<id>.response)` to serialize it.

---

### `set` — Initialize or Modify Variables

Sets variables in the workflow data context using expressions.

```yaml
- id: init_vars
  type: set
  input:
    total: 0
    prefix: "report_"
    full_name: "${data.inputs.first_name + ' ' + data.inputs.last_name}"
    items_count: "${len(data.inputs.items)}"
```

**Output:** `{ total: 0, prefix: "report_", full_name: "...", items_count: 5 }`

---

### `emit` — Send Progress Messages to the UI

Pushes real-time feedback to the user interface during long-running workflows.

```yaml
- id: notify_progress
  type: emit
  input:
    message: "Processing item ${data.steps.loop.index} of ${data.steps.loop.count}..."
    level: progress           # "thinking" | "info" | "progress" | "response"
```

| Level | Visual |
|-------|--------|
| `thinking` | Subtle animated (default) |
| `info` | Blue informational |
| `progress` | Green progress indicator |
| `response` | Highlighted, monospace — appears as assistant content |

---

### `human.input` — Pause and Wait for User Input

Pauses the workflow and prompts the user for input. The workflow resumes when the user submits a response.

#### Quick choices

```yaml
- id: approve
  type: human.input
  input:
    prompt: "The agent wants to call API X. Approve?"
    context: "${json(data.steps.plan)}"
    choices:
      - approve
      - reject
      - modify
    timeout_ms: 300000        # 5 minutes (default)
```

#### Structured form fields

```yaml
- id: user_config
  type: human.input
  input:
    prompt: "Please configure the following settings:"
    fields:
      - name: api_key
        type: string
        required: true
        description: Your API key
      - name: region
        type: select
        options: [us-east, eu-west, ap-south]
        default: us-east
      - name: max_retries
        type: string
        required: false
        default: "3"
```

**Output:** The user's response as a JSON object (e.g., `{ "response": "approve" }` or `{ "api_key": "...", "region": "eu-west", "max_retries": "3" }`).

> **Timeout:** If the user doesn't respond within `timeout_ms`, the step fails with error code `HUMAN_INPUT_TIMEOUT`.

---

### `sequence` — Run Steps Sequentially

Groups sub-steps that execute one after another.

```yaml
- id: pipeline
  type: sequence
  steps:
    - id: step_a
      type: llm.call
      input: { model: gpt-4o-mini, prompt: "Step A" }
    - id: step_b
      type: llm.call
      input: { model: gpt-4o-mini, prompt: "Continue from: ${data.steps.step_a.text}" }
```

---

### `parallel` — Run Branches in Parallel

Executes independent branches concurrently.

```yaml
- id: gather
  type: parallel
  branches:
    - steps:
        - id: fetch_weather
          type: mcp.call
          input: { server: weather, kind: tool, method: get_weather, request: { location: "Paris" } }
    - steps:
        - id: fetch_news
          type: mcp.call
          input: { server: news, kind: tool, method: get_headlines, request: { topic: "tech" } }
```

---

### `loop.sequential` — Iterate Sequentially

Loops with `while` condition or fixed `times` count.

```yaml
# Fixed count
- id: retry_loop
  type: loop.sequential
  input:
    times: 5
  steps:
    - id: attempt
      type: llm.call
      input: { model: gpt-4o-mini, prompt: "Attempt ${data.steps.retry_loop.index}" }

# While condition
- id: poll
  type: loop.sequential
  input:
    while: "${data.steps.check.status != 'ready'}"
    max_iterations: 20
  steps:
    - id: check
      type: mcp.call
      input: { server: my-server, kind: tool, method: check_status, request: {} }
```

**Loop context:** `data.steps.<loop_id>.index` (current iteration, 0-based), `data.steps.<loop_id>.count` (total completed).

---

### `loop.parallel` — Iterate in Parallel

Loops over an array of items, executing iterations concurrently.

```yaml
- id: process_all
  type: loop.parallel
  input:
    items: "${data.inputs.urls}"
    max_concurrency: 5
  steps:
    - id: fetch
      type: mcp.call
      input:
        server: http-client
        kind: tool
        method: fetch_url
        request: { url: "${data.steps.process_all.item}" }
```

**Loop context:** `data.steps.<loop_id>.item` (current item), `data.steps.<loop_id>.index`, `data.steps.<loop_id>.results` (collected results).

---

### `switch` — Conditional Branching

Two forms: expression-based and when-based.

#### Form A — Expression/value matching

```yaml
- id: route
  type: switch
  input:
    expr: "${data.steps.classify.json.category}"
  cases:
    - value: bug
      steps:
        - id: handle_bug
          type: llm.call
          input: { model: gpt-4o-mini, prompt: "Triage this bug..." }
    - value: feature
      steps:
        - id: handle_feature
          type: llm.call
          input: { model: gpt-4o-mini, prompt: "Plan this feature..." }
  default:
    - id: handle_other
      type: emit
      input: { message: "Unknown category, routing to human.", level: info }
```

#### Form B — When conditions

```yaml
- id: priority_route
  type: switch
  cases:
    - when: "${data.inputs.priority == 'critical'}"
      steps:
        - id: escalate
          type: human.input
          input: { prompt: "Critical issue! Immediate action required." }
    - when: "${data.inputs.priority == 'high'}"
      steps:
        - id: auto_handle
          type: llm.call
          input: { model: gpt-4o, prompt: "Handle high-priority: ${data.inputs.message}" }
  default:
    - id: queue
      type: emit
      input: { message: "Queued for later processing.", level: info }
```

---

### `workflow.call` — Call a Sub-Workflow

Calls another workflow defined in the same document or fetched from an external source.

#### Local call

```yaml
- id: run_analysis
  type: workflow.call
  input:
    ref:
      kind: local
      workflow: analysis       # Name of a workflow in the same document
    inputs:
      data: "${data.inputs.raw_data}"
```

#### Remote call (URL)

```yaml
- id: run_remote
  type: workflow.call
  input:
    ref:
      kind: url
      url: "https://example.com/workflows/analysis.yaml"
      workflow: main
    inputs:
      data: "${data.inputs.raw_data}"
```

---

### `workflow.plan` — Generate a Workflow Dynamically via LLM

The most powerful step type: asks an LLM to **generate a complete YAML workflow** from a natural-language instruction, then validates and compiles it before execution.

#### Basic usage

```yaml
- id: plan
  type: workflow.plan
  input:
    generator:
      model: gpt-4o
      instruction: "Build a workflow that fetches weather for Paris and summarizes it."
      context: "Available tools include weather and summarization APIs."
```

#### Full configuration

```yaml
- id: plan
  type: workflow.plan
  input:
    generator:
      model: gpt-4o                 # LLM model for planning
      provider: openai              # Optional — LLM provider
      instruction: "Analyze the user's request and build a workflow."
      context: "${json(data.inputs)}"

      # Reasoning effort for the planning LLM call (and the MCP pre-filter).
      # Defaults to "high" (max) because planning is heavy reasoning work.
      # Set to "auto" to let the provider decide, or any of:
      # "minimal" | "low" | "medium" | "high" | "max" | "auto".
      # Models without thinking support ignore this field.
      reasoning: high

      # MCP pre-filter: uses an LLM to select only relevant MCP servers/tools
      # before injecting them into the planning prompt (reduces prompt size)
      prefilter: true               # true (default) | false | { model, provider }

    # Policy constraints — restrict what the LLM can generate
    policy:
      allowed_step_types:           # Whitelist of step types
        - llm.call
        - mcp.call
        - mcp.list
        - template.render
        - set
        - emit
        - sequence
      denied_step_types:            # Blacklist (takes precedence)
        - workflow.plan             # Prevent recursive planning
      allow_remote_workflow_refs: false

    # Limits
    limits:
      max_steps_total: 20           # Maximum number of steps in the generated workflow

    # Validation
    validate:
      compile: true                 # Parse + compile the generated YAML (default: true)

    # Self-correction on failure
    on_invalid:
      action: reprompt              # "reprompt" (re-send error to LLM) | "fail"
      max_attempts: 3               # Number of attempts before giving up
```

**Output:** `{ workflow: { dsl, name, workflows: [...] }, yaml: "...", meta: { model, attempt } }`

**Features:**

- **Automatic MCP discovery**: Connects to all configured MCP servers, lists their tools/prompts, and injects them into the planning prompt so the LLM knows what's available.
- **MCP pre-filter**: Uses a lightweight LLM call to select only the MCP servers/tools relevant to the task instruction — reduces prompt size and cost.
- **Full DSL reference injection**: The LLM receives the complete DSL documentation (step types, expressions, error handling) so it can generate valid workflows.
- **Policy enforcement**: Generated workflows are validated against allowed/denied step types and max step limits.
- **Self-correction**: If the generated YAML is invalid (parse error, policy violation, compilation error), the error is sent back to the LLM for automatic correction.
- **OpenTelemetry tracing**: Full GenAI convention traces for the planning LLM call, MCP discovery, and pre-filter phases.

---

### `workflow.execute` — Execute a Planned Workflow

Executes a workflow that was dynamically generated by `workflow.plan`.

```yaml
- id: plan
  type: workflow.plan
  input:
    generator:
      model: gpt-4o
      instruction: "${data.inputs.task}"

- id: execute
  type: workflow.execute
  input:
    from_step: plan              # References the workflow.plan step that produced the YAML
```

The plan + execute pattern is the foundation of **agentic workflows**: the user describes a goal in natural language, the LLM plans the steps, and the engine executes them.

---

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

---

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
        items: { type: string }
        description: Extracted tags

      # Typed object
      report:
        expr: "${data.steps.build.report}"
        type: object
        properties:
          title: { type: string }
          score: { type: number }
        description: Structured report

      # Dictionary
      metrics:
        expr: "${data.steps.collect.metrics}"
        type: dictionary
        additionalProperties: { type: number }
        description: Named metrics map
```

### JSON Schema generation

`OutputDef` types are convertible to JSON Schema via `JsonSchemaConverter.OutputsToJsonSchema(outputs)`, used for MCP tool exposure and API documentation.

---

## Expressions `${...}`

Expressions are embedded in strings using `${...}` syntax. They are JavaScript-style expressions evaluated by the in-tree JS-subset interpreter in `gnougo_flow_core._jsmini`.

### Data access

- `data.inputs.*` — workflow input parameters
- `data.steps.<step_id>.*` — output of a previously executed step
- `data.env.*` — environment variables
- Optional chaining: `data.steps.maybe_skipped?.value`

### Operators

`&& || ! == != < <= > >= + - * / % ??`

### Built-in functions

| Function | Description |
|----------|-------------|
| `exists(val)` | `true` if val is non-null |
| `coalesce(a, b, ...)` | Returns first non-null argument |
| `len(val)` | Length of string or array (0 for null) |
| `length(val)` | Alias for `len(val)` |
| `lower(s)` | Lowercase string |
| `upper(s)` | Uppercase string |
| `trim(s)` | Trims whitespace |
| `contains(s, sub)` | `true` if string `s` contains `sub` |
| `startsWith(s, prefix)` | `true` if `s` starts with prefix |
| `endsWith(s, suffix)` | `true` if `s` ends with suffix |
| `replace(s, old, new)` | Replaces all occurrences |
| `substring(s, start)` | Characters from position `start` to end |
| `substring(s, start, len)` | `len` characters starting at `start` |
| `toNumber(val)` | Converts to number |
| `json(val)` | Serializes value to JSON string |
| `fromJson(s)` | Parses a JSON string into a node |
| `now()` | Returns the current local date/time as an ISO-8601 string |
| `base64(val)` | Encodes the UTF-8 string value as Base64 |
| `formatDate(dateStr, fmt)` | Formats a date string (default: `yyyy-MM-dd`) |

### JavaScript-style expression support

- Ternary: `${data.inputs.mode == "fast" ? 0.0 : 0.7}`
- Template literals: `` ${`Hello ${data.inputs.name}`} ``
- Array methods: `${data.inputs.items.filter(i => i.active).length}`

---

## WFScript — Custom JavaScript Functions

Define reusable functions in the `functions:` block (document-level or workflow-level):

```yaml
dsl: 1
name: smart-triage
functions: |
  function classify(text) {
    if (contains(lower(text), "urgent")) return "critical";
    if (contains(lower(text), "bug")) return "bug";
    return "general";
  }

  function truncate(text, maxLen) {
    if (len(text) <= maxLen) return text;
    return text.substring(0, maxLen) + "...";
  }

workflows:
  main:
    inputs:
      message: { type: string, required: true }
    steps:
      - id: route
        type: switch
        input:
          expr: "${functions.classify(data.inputs.message)}"
        cases:
          - value: critical
            steps:
              - id: escalate
                type: human.input
                input:
                  prompt: "URGENT: ${functions.truncate(data.inputs.message, 100)}"
          - value: bug
            steps:
              - id: triage_bug
                type: llm.call
                input:
                  model: gpt-4o-mini
                  prompt: "Triage this bug report: ${data.inputs.message}"
```

---

## Error Handling

### Retry

Automatically retries a step on transient (retryable) errors:

```yaml
retry:
  max: 3                 # Maximum attempts
  backoff_ms: 1000       # Initial delay between retries
  backoff_mult: 2.0      # Multiplier for exponential backoff
  jitter_ms: 100         # Random jitter added to each delay
```

### on_error

Evaluated **after retries are exhausted** (or immediately for non-retryable errors):

```yaml
on_error:
  cases:
    - if: "${error.code == \"LLM_TIMEOUT\" || error.code == \"LLM_NETWORK\"}"
      action: continue
      set_output:
        text: "Temporary LLM issue — using fallback"
    - if: "${error.code == \"INPUT_VALIDATION\"}"
      action: stop          # Stop the workflow immediately
    - action: stop          # Default: stop on unknown errors
```

**Error context variables:** `error.code`, `error.message`, `error.retryable`, `step.id`, `step.type`

**Actions:** `continue` (skip the step, optionally set a fallback output) | `stop` (abort the workflow)

### Common error codes

| Code | Retryable | Description |
|------|-----------|-------------|
| `INPUT_VALIDATION` | No | Missing or malformed input |
| `LLM_TIMEOUT` | Yes | LLM request timed out |
| `LLM_NETWORK` | Yes | Network error reaching the LLM |
| `MCP_CONNECTION_ERROR` | Yes | Cannot connect to MCP server |
| `MCP_TOOL_ERROR` | No | MCP tool returned an error |
| `TEMPLATE_PLAN` | No | `workflow.plan` failed to generate valid YAML |
| `TEMPLATE_POLICY` | No | Generated workflow violates policy constraints |
| `HUMAN_INPUT_TIMEOUT` | No | User didn't respond within `timeout_ms` |
| `NO_HITL_PROVIDER` | No | No human input provider configured |

### Full example — resilient LLM call with fallback

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
          text: "Summary temporarily unavailable."
      - action: stop
```

---

## CLI

The published package exposes the `gnougo-flow` command.

```bash
# Validate a workflow (check syntax, types, compilation)
gnougo-flow validate examples/triage.yaml
# Inspect the structure (workflows, steps, inputs, outputs)
gnougo-flow inspect examples/triage.yaml
# Execute with key=value inputs
gnougo-flow run examples/triage.yaml -i message=hello -i priority=normal
# Execute with full JSON input
gnougo-flow run examples/triage.yaml -j '{"message":"hello","priority":"normal"}'
# Execute with full JSON input loaded from a file
gnougo-flow run examples/triage.yaml -j @inputs.json
```

When running directly from the repository with `uv`, prefix commands with `uv run`:

```bash
uv run gnougo-flow validate examples/triage.yaml
uv run gnougo-flow inspect examples/triage.yaml
uv run gnougo-flow run examples/triage.yaml -i message=hello
```

---

## Python Runtime Notes

The Python package is not a NativeAOT binary; it is a Python 3.10+ library and CLI. It still follows the same design goals as `GnOuGo.Flow.Core`:

- YAML parsing uses PyYAML and typed Python models.
- JSON-like workflow data stays in Python dictionaries/lists/scalars.
- Templating is implemented in-tree with a minimal Mustache-compatible renderer.
- Expression interpolation and WFScript use `gnougo_flow_core._jsmini`, an in-tree JavaScript-subset interpreter with execution limits.
- Runtime services are injected through protocols instead of concrete infrastructure dependencies.
- MCP helpers live in `gnougo_flow_core.integrations`:
  - `InMemoryMcpClientFactory` and `MockMcpServerConfig` for tests and demos.
  - `ConfiguredMcpClientFactory` and `McpSessionAdapter` for injected MCP sessions.
  - `RoutingLLMClientAdapter` for adapting a routing LLM client.
- `WorkflowEngine.mcp_cache` defaults to `McpCacheHelper`, a 5-minute sliding TTL cache for MCP tools/resources/prompts per server. Set it to `None` to disable capability caching.
- `WorkflowEngine.resume_async`, `WorkflowCheckpointer`, and `limits.run_id` support resumable workflow execution.
Development commands:

```bash
uv sync --extra dev
uv run --extra dev pytest
uv run --extra dev ruff check .
python -m pip install --upgrade build
python -m build
```

The release pipeline injects the generated repository version into `pyproject.toml` before building and publishing the package to PyPI.
