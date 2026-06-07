# gnougo-flow-core — YAML Workflow DSL Engine (Python)

<a href="https://pypi.org/project/gnougo-flow-core/"><img src="https://img.shields.io/pypi/v/gnougo-flow-core.svg" alt="PyPI version"></a>
<a href="https://pypi.org/project/gnougo-flow-core/"><img src="https://img.shields.io/pypi/pyversions/gnougo-flow-core.svg" alt="Supported Python versions"></a>
<a href="https://pepy.tech/projects/gnougo-flow-core"><img src="https://static.pepy.tech/badge/gnougo-flow-core" alt="PyPI downloads"></a>

Python 3.10+ implementation of `GnOuGo.Flow.Core`, the declarative YAML workflow DSL engine.
Write YAML workflows that orchestrate LLMs, MCP servers, templates, loops, human input, and dynamic code generation — all from a single file.

---

## Package Status and Parity

The .NET library at [`src/GnOuGo.Flow.Core/`](../../../src/GnOuGo.Flow.Core/) is the **source of truth**.
This Python package mirrors its public surface as closely as Python idioms allow. See [`PORTING_TODO.md`](PORTING_TODO.md) for the detailed parity log and remaining work items.

| Area | Status |
|---|---|
| YAML DSL parser (`version:`) | Yes |
| Validation + compilation pipeline | Yes |
| Expression interpolation `${...}` + built-in functions | Yes (AST-based JS-subset interpreter) |
| Mustache `template.render` engine | Yes |
| WFScript (`functions:` block) | Yes multi-statement (`var`/`let`/`const`, `if`/`else`, `return`) |
| Runtime engine + step registry | Yes |
| Step types: `set`, `emit`, `sequence`, `parallel`, `loop.sequential`, `loop.parallel`, `switch`, `template.render`, `llm.call`, `mcp.list`, `mcp.call`, `human.input`, `workflow.call`, `workflow.plan`, `workflow.execute` | Yes |
| MCP integrations (`InMemoryMcpClientFactory`, `ConfiguredMcpClientFactory`, cache helper) | Yes |
| MCP `progressEvents` -> thinking telemetry + stdio JSONL real-time progress | Yes |
| MCP server-level `DiscoveryTimeoutSeconds` / `CallTimeoutSeconds` metadata | Yes |
| `LLMRequest.reasoning` field | Yes |
| Model metadata catalog (pricing, token limits, capabilities, overrides) | Yes |
| `workflow.plan` defaults `reasoning="high"` | Yes |
| `workflow.plan` validator + semantic mapping checks | Yes |
| MCP tool `output_schema` / `example_response` planning contracts | Yes |
| Workflow source telemetry (`source_text` / `source_format`) | Yes |
| `JsonSchemaConverter` (inputs/outputs to JSON Schema) | Yes |
| `WorkflowCheckpointer` + `WorkflowEngine.resume_async` | Yes |
| CLI: `validate` / `inspect` / `run` subcommands | Yes |

---

## Table of Contents

- [Package Status and Parity](#package-status-and-parity)
- [Architecture](#architecture)
- [Get Started — One-file with mocks](#get-started--one-file-with-mocks)
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
- [Model Metadata Catalog](#model-metadata-catalog)
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

## Get Started — One-file with mocks

This example is a complete Python script that runs fully locally: the LLM client and MCP server are mocked in memory, so no API key, network call, or external MCP process is required.

Install the package:

```bash
python -m pip install gnougo-flow-core
```

Create `one_file_flow.py`:

```python
import asyncio
import json

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.integrations import InMemoryMcpClientFactory, MockMcpServerConfig
from gnougo_flow_core.models import LLMResponse, McpCallResult, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine, apply_workflow_input_defaults

WORKFLOW_YAML = """
version: 1
name: one-file-mocked-flow
workflows:
  main:
    inputs:
      topic: { type: string, required: true }
    steps:
      - id: discover
        type: mcp.list
        input:
          servers: [demo]
          include: ["tools"]
      - id: facts
        type: mcp.call
        input:
          server: demo
          kind: tool
          method: get_facts
          request:
            topic: "${data.inputs.topic}"
      - id: summarize
        type: llm.call
        input:
          model: mock-gpt
          prompt: "Summarize these facts as one sentence: ${json(data.steps.facts.response)}"
      - id: final
        type: template.render
        input:
          engine: mustache
          template: "{{summary}}"
          data:
            summary: "${data.steps.summarize.text}"
          mode: text
    outputs:
      answer: "${data.steps.final.text}"
      tools_seen: "${len(data.steps.discover.tools)}"
      facts: "${data.steps.facts.response}"
"""


class MockLLMClient:
    async def call_async(self, request):
        return LLMResponse(
            text=f"[Mock {request.model}] Summary generated from MCP facts.",
            usage={"prompt_tokens": 12, "completion_tokens": 18, "total_tokens": 30},
        )


def build_mcp_factory() -> InMemoryMcpClientFactory:
    factory = InMemoryMcpClientFactory()

    def get_facts(arguments):
        topic = (arguments or {}).get("topic", "unknown")
        return McpCallResult(
            is_error=False,
            content={
                "topic": topic,
                "facts": [
                    f"{topic} is handled by a mocked MCP tool.",
                    "No network or external service is required.",
                ],
            },
        )

    factory.register_server(
        "demo",
        MockMcpServerConfig(
            description="A mock knowledge server",
            tools=[
                McpToolInfo(
                    name="get_facts",
                    description="Returns deterministic facts for a topic",
                    input_schema={
                        "type": "object",
                        "properties": {"topic": {"type": "string"}},
                        "required": ["topic"],
                    },
                    output_schema={
                        "type": "object",
                        "properties": {
                            "topic": {"type": "string"},
                            "facts": {"type": "array", "items": {"type": "string"}},
                        },
                        "additionalProperties": False,
                    },
                )
            ],
            tool_handlers={"get_facts": get_facts},
        ),
    )
    return factory


async def main() -> None:
    document = WorkflowParser.parse(WORKFLOW_YAML)
    compiled = WorkflowCompiler().compile(document)
    workflow = compiled.workflows[compiled.entrypoint]

    engine = WorkflowEngine()
    engine.llm_client = MockLLMClient()
    engine.mcp_client_factory = build_mcp_factory()

    inputs = apply_workflow_input_defaults(workflow.source, {"topic": "GnOuGo.Flow"})
    result = await engine.execute_async(workflow, inputs)

    if not result.success:
        message = result.error.message if result.error else "unknown error"
        raise RuntimeError(f"Workflow failed: {message}")

    print(json.dumps(result.outputs, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main())
```

Run it:

```bash
python one_file_flow.py
```

Expected output shape:

```json
{
  "answer": "[Mock mock-gpt] Summary generated from MCP facts.",
  "tools_seen": 1,
  "facts": {
    "topic": "GnOuGo.Flow",
    "facts": [
      "GnOuGo.Flow is handled by a mocked MCP tool.",
      "No network or external service is required."
    ]
  }
}
```

When developing inside this repository, you can run against the local source tree instead of the published package:

```powershell
$env:PYTHONPATH = "C:\github\GnouGo\librairies\python\gnougo-flow-core\src"
python one_file_flow.py
```

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
    temperature: 0.7                                 # Optional override; omit by default
    max_tokens: 2048                                 # Optional
    reasoning: auto                                  # Optional — auto|minimal|low|medium|high|max
                                                     # Default: omitted (provider decides).
                                                     # Unsupported optional fields are removed by runtime metadata.
```

`temperature`, `reasoning`, `structured_output`, and tool-calling support are checked against the runtime model metadata catalog before the configured LLM client is called. For example, a request to `o4-mini` with `temperature: 0.7` is automatically sent without `temperature`.

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

`timeout_ms` is treated as the workflow-requested timeout. When the configured MCP server metadata includes `DiscoveryTimeoutSeconds`, the effective timeout is the maximum of `timeout_ms` and the server-level value, matching the .NET behavior that prevents generated workflows from undercutting known-slow MCP servers.

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

#### MCP progress events -> thinking telemetry

The Python runtime mirrors the .NET `GnOuGo.Flow.Core` progress contract. For stdio MCP transports, `ConfiguredMcpClientFactory.capture_stdio_error_line(...)` can receive structured JSONL stderr messages with this shape while the tool is still running:

```json
{
  "type": "gnougo.mcp.progress",
  "server": "GnOuGo.GithubCopilot.Mcp",
  "method": "code_agent_edit",
  "kind": "tool",
  "event": {
    "kind": "session_create",
    "level": "thinking",
    "message": "Creating Copilot agent session.",
    "timestamp": "2026-05-20T10:00:00Z",
    "file": "src/Program.cs"
  }
}
```

Matching messages are forwarded immediately as `gnougo-flow.step.thinking` telemetry events. As a fallback/history mechanism, `mcp.call` also scans the final tool response for `progressEvents` (aliases accepted: `progress_events`, `progress`, `events`) and forwards each item the same way. Real-time events are deduplicated against final fallback events.

`progressEvents` is the stable GnOuGo-facing contract. MCP servers may map provider-specific or SDK-specific events into this schema, but the Python Flow runtime does not depend on native SDK event types.

`timeout_ms` is treated as the workflow-requested call timeout. When the configured MCP server metadata includes `CallTimeoutSeconds`, the effective timeout is the maximum of `timeout_ms` and the server-level value.

#### Output access patterns

| Mode | Access |
|------|--------|
| Single tool | `data.steps.<id>.status`, `data.steps.<id>.response` |
| Single prompt | `data.steps.<id>.status`, `data.steps.<id>.text` |
| Batch/auto | `data.steps.<id>.results` (array) |
| LLM-assisted | `data.steps.<id>.text`, `data.steps.<id>.json` |

> **Important:** The `response` object is tool-specific. `workflow.plan` treats single-tool MCP responses as opaque unless the tool advertises `output_schema` or `example_response`. Access `data.steps.<id>.response.<field>` only for documented fields. Otherwise pass the whole response with `json(data.steps.<id>.response)` or add an `llm.call` normalization step with `structured_output`.

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

Calls another workflow defined in the same document, fetched from an external source, or loaded from the configured workspace root.
Resolution is delegated to `WorkflowEngine.workflow_call_resolver` (`DefaultWorkflowCallResolver` by default), so applications can add their own `kind` values without replacing the executor.

#### Local call

```yaml
- id: run_analysis
  type: workflow.call
  input:
    ref:
      kind: local
      name: analysis       # Name of a workflow in the same document
    args:
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
      export: main         # Optional exported workflow name
    args:
      data: "${data.inputs.raw_data}"
```

Configure `DefaultWorkflowCallResolver(allowed_hostnames=[...])` or `WorkflowEngine.fetch_policy.allowed_hostnames` to restrict URL hosts. `FetchPolicy.require_https` remains enabled by default.

#### Workspace call

```yaml
- id: run_workspace
  type: workflow.call
  input:
    ref:
      kind: workspace
      path: workflows/analysis.yaml
      export: main         # Optional
    args:
      data: "${data.inputs.raw_data}"
```

`workspace` paths are resolved relative to the workspace root injected into `DefaultWorkflowCallResolver`. Absolute paths and path traversal outside that root are rejected.

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
- **Full validation before acceptance**: `workflow.plan` runs the validator, compiler, and semantic checks before returning a plan. This catches non-fatal validator diagnostics such as unknown step types, invalid container shapes, future step references, conditional branch/loop mapping errors, and invalid `data.steps.<id>.response.<field>` mappings.
- **MCP output contracts**: MCP discovery injects complete `input_schema`, `output_schema`, and `example_response` metadata into the planning prompt. `output_schema` / `example_response` define which fields may be read from `mcp.call` single-tool `response` objects.
- **Self-correction**: If the generated YAML is invalid (parse error, policy violation, compilation error, or semantic mapping error), the error is sent back to the LLM for automatic correction.
- **OpenTelemetry tracing**: Full GenAI convention traces for the planning LLM call, MCP discovery, and pre-filter phases.

**Semantic mapping guardrails:** generated plans must not read `data.steps.<id>.*` from steps produced only inside a `switch` case, an `if`-guarded step, or a loop body unless that value is first mapped into a guaranteed location. Function arguments are evaluated eagerly, so `coalesce(data.steps.fix.value, data.steps.question.value)` is still unsafe when either step may not have executed. Prefer a common workflow-level output alias in every branch, or a guaranteed normalization step with a stable output schema.

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
| `pick(obj, ...keys)` | Returns a new object containing only the requested keys; keys may be separate arguments or an array |
| `omit(obj, ...keys)` | Returns a new object with the requested keys removed; keys may be separate arguments or an array |
| `fromJson(s)` | Parses a JSON string into a node |
| `now()` | Returns the current local date/time as an ISO-8601 string |
| `base64(val)` | Encodes the UTF-8 string value as Base64 |
| `formatDate(dateStr, fmt)` | Formats a date string (default: `yyyy-MM-dd`) |

### JavaScript-style expression support

- Ternary: `${data.inputs.mode == "fast" ? 0.0 : 0.7}`
- Template literals: `` ${`Hello ${data.inputs.name}`} ``
- Array methods: `${data.inputs.items.filter(i => i.active).length}`

### Runtime limits

Expression evaluation is sandboxed through `ExecutionLimits`:

| Property | Default | Description |
|----------|---------|-------------|
| `max_expression_ast_nodes` | `500` | Parser/validator complexity limit. |
| `max_expression_statements` | `100000` | JS-subset interpreter statement budget. |
| `expression_timeout_seconds` | `15` | Evaluation timeout. |
| `expression_memory_limit_bytes` | `50000000` | Parity configuration value; the Python in-tree interpreter currently enforces node/statement/time/call-depth limits. |

Increase these limits only for trusted workflows; prefer simplifying expressions or moving complex logic to WFScript functions.

---

## WFScript — Custom JavaScript Functions

Define reusable functions in the `functions:` block (document-level or workflow-level):

```yaml
version: 1
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

## Model Metadata Catalog

The Python runtime includes a model metadata catalog aligned with the .NET implementation. It centralizes:

- token limits: `context_window_tokens`, `max_input_tokens`, `max_output_tokens`
- pricing: `input_per_1m_tokens`, `output_per_1m_tokens`
- capabilities: temperature, reasoning effort, structured output, tools, JSON mode, vision, embeddings
- aliases and user-provided extensions

When the package is used inside the GnOuGo mono-repo, the Python runtime automatically reads the shared builtin catalog from `src/GnOuGo.AI.Core/Telemetry/model-metadata.json`. This keeps the Python and .NET providers aligned on provider-specific limits, pricing, and capabilities.

`WorkflowEngine.sanitize_llm_request()` removes unsupported optional request fields before calling the configured LLM client. This prevents provider crashes such as sending `temperature` to reasoning models that reject it.

Pricing uses the same metadata resolver. `try_get_pricing()` and `estimate_cost()` read builtin pricing by default and can also use `LLMOptions.model_metadata_files` / `LLMOptions.model_overrides` when passed explicitly.

```python
from gnougo_flow_core import WorkflowEngine, LLMOptions, LLMModelMetadata, ModelCapabilityMetadata

engine = WorkflowEngine()
engine.llm_options = LLMOptions(
    model_metadata_files=["config/my-models.json"],
    model_overrides={
        "my-local-model:latest": LLMModelMetadata(
            provider_type="ollama",
            context_window_tokens=32768,
            max_output_tokens=8192,
            capabilities=ModelCapabilityMetadata(
                supports_temperature=True,
                supports_reasoning_effort=False,
                supports_structured_output=False,
                supports_tools=False,
            ),
        )
    },
)
```

External metadata files can also use .NET-style camelCase field names and provider-qualified keys such as `openai/gpt-4o` or `copilot/gpt-4o` when the same model id exists on multiple providers:

```jsonc
{
  "models": {
    "openai/model-id": {
      "providerType": "openai",
      "contextWindowTokens": 128000,
      "maxOutputTokens": 16384,
      "pricing": { "inputPer1MTokens": 0.15, "outputPer1MTokens": 0.60 },
      "capabilities": {
        "supportsTemperature": true,
        "supportsReasoningEffort": false,
        "supportsStructuredOutput": true,
        "supportsTools": true
      }
    }
  },
  "aliases": { "short-name": "openai/model-id" }
}
```

Metadata precedence is:

```text
builtin catalog < model_metadata_files < model_overrides < heuristics for missing fields
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
