# GnOuGo.Flow — YAML Workflow DSL Engine

<a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/v/GnOuGo.Flow.Core.svg" alt="NuGet version"></a>
<a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt=".NET 10.0"></a>
<a href="https://nugettrends.com/packages?ids=GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/dt/GnOuGo.Flow.Core.svg" alt="NuGet downloads"></a>

Declarative workflow engine based on a YAML DSL, **NativeAOT**-compatible (.NET 10).
Write YAML workflows that orchestrate LLMs, MCP servers, templates, loops, human input, and dynamic code generation — all from a single file.

---

## Table of Contents

- [Architecture](#architecture)
- [Get Started — One-file with mocks](#get-started--one-file-with-mocks)
- [Quick Start](#quick-start)
- [Document Structure](#document-structure)
- [Skill Metadata](#skill-metadata)
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
  - [workflow.route](#workflowroute--route-to-workflow-candidates)
  - [workflow.plan](#workflowplan--generate-a-workflow-dynamically-via-llm)
  - [workflow.execute](#workflowexecute--execute-a-planned-workflow)
- [Typed Inputs](#typed-inputs)
- [Typed Outputs](#typed-outputs)
- [Expressions `${...}`](#expressions-)
- [WFScript — Custom JavaScript Functions](#wfscript--custom-javascript-functions)
- [Error Handling](#error-handling)
- [CLI](#cli)
- [NativeAOT](#nativeaot)

---

## Architecture

```
src/
  GnOuGo.Flow.Core/          # Core library (publishable package)
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
  GnOuGo.Flow.Server/        # HTTP API + React/Vite front-end
tests/
  GnOuGo.Flow.Tests/         # Unit tests
```

Flow keeps one internal type representation for validation: `FlowTypeDescriptor`.
Workflow `InputDef`/`OutputDef`, executor `StepContract` schemas, MCP JSON Schema, and workflow.plan contract snippets are converted into or out of this descriptor instead of being reasoned about as separate type systems.

During workflow.plan semantic validation, a `WorkflowSymbolTable` is built as steps are walked. It tracks workflow inputs, scoped data variables, available step output types, and control-flow availability so expressions such as `data.steps.<id>.<field>` and loop-local `data.<item_var>.<field>` can be checked against known symbols before generated YAML is accepted.

Step outputs are resolved through `StepOutputTypeResolver`: each step starts from its executor contract and can be refined by static input, such as `set.output_schema`, `llm.call.input.structured_output`, direct MCP tool output schemas, local `workflow.call` targets, `template.render` mode, `human.input` form fields, and loop body output snapshots.

---

## Skill Metadata

A workflow document can advertise routing metadata through a top-level `skill` block. Hosts can parse this lightweight card for catalogs without compiling the workflow.

```yaml
version: 1
name: document-agent
skill:
  description: Answers questions over indexed local documents.
  tags: [documents, rag, search]
  inputs:
    prompt: { type: string, required: true }
    history: { type: array, required: false }
  outputs:
    answer: { type: string }
workflows:
  main:
    steps:
      - id: answer
        type: llm.call
        input:
          prompt: "${data.inputs.prompt}"
```

`skill` is descriptive metadata only. Runtime validation still uses each workflow's own `inputs` and `outputs`.

---

## Get Started — One-file with mocks

This example is a complete `Program.cs` that runs fully locally: the LLM client and MCP server are mocked in memory, so no API key, network call, or external MCP process is required.

Create a tiny console app and add `GnOuGo.Flow.Core`:

```powershell
dotnet new console -n FlowOneFileDemo
Set-Location FlowOneFileDemo
dotnet add package GnOuGo.Flow.Core
```

Replace `Program.cs` with this one-file implementation:

```csharp
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

const string workflowYaml = """
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
""";

var document = WorkflowParser.Parse(workflowYaml);
var compiled = new WorkflowCompiler().Compile(document);
var workflow = compiled.Workflows[compiled.Entrypoint ?? "main"];

var mcp = new InMemoryMcpClientFactory();
mcp.RegisterServer("demo", new MockMcpServerConfig
{
    Description = "A mock knowledge server",
    Tools =
    [
        new McpToolInfo
        {
            Name = "get_facts",
            Description = "Returns deterministic facts for a topic",
            InputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "topic": { "type": "string" } },
              "required": ["topic"]
            }
            """),
            OutputSchema = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "topic": { "type": "string" },
                "facts": { "type": "array", "items": { "type": "string" } }
              },
              "additionalProperties": false
            }
            """)
        }
    ],
    ToolHandlers =
    {
        ["get_facts"] = args =>
        {
            var topic = args?["topic"]?.GetValue<string>() ?? "unknown";
            return new McpCallResult
            {
                IsError = false,
                Content = new JsonObject
                {
                    ["topic"] = topic,
                    ["facts"] = new JsonArray(
                        $"{topic} is handled by a mocked MCP tool.",
                        "No network or external service is required.")
                }
            };
        }
    }
});

var engine = new WorkflowEngine
{
    LLMClient = new MockLLMClient(),
    McpClientFactory = mcp
};

var inputs = WorkflowInputDefaults.Apply(workflow.Source, new JsonObject
{
    ["topic"] = "GnOuGo.Flow"
});

var result = await engine.ExecuteAsync(workflow, inputs, CancellationToken.None);

if (!result.Success)
{
    Console.Error.WriteLine($"Workflow failed: {result.Error?.Code} - {result.Error?.Message}");
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine(result.Outputs?.ToJsonString(new System.Text.Json.JsonSerializerOptions
{
    WriteIndented = true
}));

internal sealed class MockLLMClient : ILLMClient
{
    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        return Task.FromResult(new LLMResponse
        {
            Text = $"[Mock {request.Model}] Summary generated from MCP facts.",
            Usage = new JsonObject
            {
                ["prompt_tokens"] = 12,
                ["completion_tokens"] = 18,
                ["total_tokens"] = 30
            }
        });
    }
}
```

Run it:

```powershell
dotnet run
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

When developing inside this repository, you can use a `ProjectReference` to `src/GnOuGo.Flow.Core/GnOuGo.Flow.Core.csproj` instead of the NuGet package.

---

## Quick Start

Install the .NET package:

```bash
dotnet add package GnOuGo.Flow.Core
```

Build a local package for validation:

```bash
dotnet pack src/GnOuGo.Flow.Core/GnOuGo.Flow.Core.csproj -c Release -o artifacts/packages/nuget /p:PackageVersion=0.1.0-local
```

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

Run it:

```bash
dotnet run --project src/GnOuGo.Flow.Cli -- run hello.yaml -i 'name=World'
```

---

## Document Structure

Every workflow file starts with:

```yaml
version: 1                    # Workflow document version (required, always 1)
name: my-workflow             # Document name (optional)
functions: |                  # Global WFScript functions (optional)
  function myHelper(x) { return x * 2; }

workflows:
  main:                       # Entrypoint workflow (by convention)
    functions: |              # Workflow-local WFScript functions (optional)
      function localHelper(x) { return x + 1; }
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
Document-level functions are inherited by every workflow. Workflow-level functions are scoped to that workflow execution and can shadow document-level functions with the same name.

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
        required: [category, priority, confidence]
        additionalProperties: false
      strict: true
```

**Output:** `{ text: "...", json: { category: "bug", priority: "high", confidence: 0.92 }, usage: {...} }`

Access: `data.steps.classify.json.category`, `data.steps.classify.json.priority`

Before contacting the provider, Flow validates the `structured_output` envelope and recursively validates/normalizes its JSON Schema. `schema_inline` and `schema_ref` are mutually exclusive; `schema_ref` must resolve through an expression to a schema object. With `strict: true`, the root must be an object, every object property must be listed in `required`, every object must set `additionalProperties: false`, arrays must declare `items`, and unsupported strict composition keywords are rejected. After the LLM responds, parsed JSON is validated against the same schema before it is exposed as `data.steps.<id>.json`; failures use `LLM_SCHEMA` and include property paths.

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

> **Important:** The `response` object is tool-specific. `workflow.plan` treats single-tool MCP responses as opaque unless the tool advertises `OutputSchema` or `ExampleResponse`. Access `data.steps.<id>.response.<field>` only for documented fields. Otherwise pass the whole response with `json(data.steps.<id>.response)` or add an `llm.call` normalization step with `structured_output`.
>
> When an MCP server returns protocol `structuredContent`, `mcp.call` uses that value as `response`. `workflow.plan` can include and validate fields inside that response only when the same tool is discoverable with an `OutputSchema` or representative `ExampleResponse`.

#### MCP progress events → thinking telemetry

For stdio MCP servers, `mcp.call` also listens to structured JSONL progress messages written on stderr while the tool is still running. Matching events are forwarded immediately as `gnougo-flow.step.thinking` telemetry events. As a fallback/history mechanism, when the final tool result contains a `progressEvents` array (also accepted: `progress_events`, `progress`, or `events`), `mcp.call` forwards each item the same way. Agent Server can stream these as `thinking:<level>` UI events.

`progressEvents` is the stable GnOuGo-facing contract. MCP servers may map provider-specific or SDK-specific events into this schema, but `GnOuGo.Flow.Core` does not depend on those native event types.

Expected item shape:

```json
{
  "kind": "session_create",
  "level": "thinking",
  "message": "Creating Copilot agent session.",
  "timestamp": "2026-05-19T00:00:00Z",
  "file": "src/Program.cs"
}
```

Only the `message` field is required. These messages are operational progress milestones and should not contain raw model chain-of-thought.

---

### `set` — Initialize or Modify Variables

Sets variables in the workflow data context using expressions.

```yaml
- id: init_vars
  type: set
  output_schema:
    type: object
    properties:
      total: { type: integer }
      prefix: { type: string }
      full_name: { type: string }
      items_count: { type: integer }
    required: [total, prefix, full_name, items_count]
    additionalProperties: false
  input:
    total: 0
    prefix: "report_"
    full_name: "${data.inputs.first_name + ' ' + data.inputs.last_name}"
    items_count: "${len(data.inputs.items)}"
```

**Output:** `{ total: 0, prefix: "report_", full_name: "...", items_count: 5 }`

`output_schema` is optional, but recommended for any `set` step that normalizes or reshapes data for later steps. When present, workflow.plan validates `input` against the schema, downstream references use the declared output type, and the runtime verifies the resolved output before exposing it as `data.steps.<id>`.

---

### `emit` — Send Progress Messages to the UI

Pushes real-time feedback to the user interface during long-running workflows.

```yaml
- id: notify_progress
  type: emit
  input:
    message: "Processing item ${data._loop.index} of ${data.steps.loop.count}..."
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
    mode: choice
    prompt: "The agent wants to call API X. Approve?"
    context: "${json(data.steps.plan)}"
    choices:
      - approve
      - reject
      - modify
    timeout_ms: 36000000      # 10 hours (default)
```

#### Structured form fields

```yaml
- id: user_config
  type: human.input
  input:
    mode: form
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

**Modes:** `text`, `choice`, `form`, `confirm`. When omitted, the engine infers `form` from `fields`, `choice`/`confirm` from `choices`, otherwise `text`.

**Field types:** `string`, `text`, `textarea`, `markdown`, `json`, `yaml`, `number`, `integer`, `boolean`, `select`, `radio`, `multiselect`, `checkbox`, `password`, `secret`, `url`, `email`, `date`, `file`, `directory`.

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

Loops sequentially with `times`, `while`, or `items`. Supports `item_var` and `index_var` for item iteration (same interface as `loop.parallel`).

```yaml
# Fixed count
- id: retry_loop
  type: loop.sequential
  input:
    times: 5
  steps:
    - id: attempt
      type: llm.call
      input: { model: gpt-4o-mini, prompt: "Attempt ${data._loop.index}" }

# While condition
- id: poll
  type: loop.sequential
  input:
    while: "${data.steps.check.status != 'ready'}"
    max_times: 20
  steps:
    - id: check
      type: mcp.call
      input: { server: my-server, kind: tool, method: check_status, request: {} }

# Iterate over items (same interface as loop.parallel)
- id: process_each
  type: loop.sequential
  input:
    items: "${data.inputs.urls}"
  item_var: url
  index_var: idx
  steps:
    - id: fetch
      type: mcp.call
      input:
        server: http-client
        kind: tool
        method: fetch_url
        request: { url: "${data.url}" }
```

| Input field | Type | Description |
|---|---|---|
| `times` | number | Fixed iteration count (mutually exclusive with `items`) |
| `items` | array | Array to iterate over (mutually exclusive with `times`) |
| `while` | string | Expression evaluated before each iteration; stops when falsy |
| `max_times` | number | Hard cap on iterations (default: engine limit) |

| Step field | Type | Default | Description |
|---|---|---|---|
| `item_var` | string | `"item"` | Variable name for current item in `data.<item_var>` |
| `index_var` | string | `"i"` | Variable name for current index in `data.<index_var>` |

**Loop context:** `data._loop.index` (0-based iteration index), `data._loop.item` (current item when using `items`).

**Output:** `{ results: [...], count: N }` — each element in `results` contains the step outputs (`data.steps.*`) for that iteration.

During workflow.plan validation, `items`/`over` sources are used to infer `data.<item_var>` and `data._loop.item`; `data.<index_var>` and `data._loop.index` are typed as integers. For `times`/`while` loops without items, `data._loop.index` and `data.loop.index` are typed as integers inside the loop body.

---

### `loop.parallel` — Iterate in Parallel

Loops over an array of items, executing iterations concurrently.

```yaml
- id: process_all
  type: loop.parallel
  input:
    items: "${data.inputs.urls}"
    max_concurrency: 5
  item_var: url
  index_var: idx
  steps:
    - id: fetch
      type: mcp.call
      input:
        server: http-client
        kind: tool
        method: fetch_url
        request: { url: "${data.url}" }
```

| Input field | Type | Description |
|---|---|---|
| `items` | array | **Required** — array to iterate over |
| `max_concurrency` | number | Optional max parallel branches (0 = unlimited) |

| Step field | Type | Default | Description |
|---|---|---|---|
| `item_var` | string | `"item"` | Variable name for current item in `data.<item_var>` |
| `index_var` | string | `"i"` | Variable name for current index in `data.<index_var>` |

**Loop context:** `data._loop.index`, `data._loop.item`, `data.<item_var>`, `data.<index_var>`.

**Output:** `{ results: [...], count: N }` — each element in `results` contains the step outputs for that iteration.

During workflow.plan validation, the item source is used to infer `data.<item_var>` and `data._loop.item`; `data.<index_var>` and `data._loop.index` are typed as integers inside the loop body.

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
          input: { mode: text, prompt: "Critical issue! Immediate action required." }
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

Calls another workflow through one canonical shape:

- `input.ref` identifies the target workflow.
- `input.args` provides the target workflow inputs.
- The called workflow result is stored in `data.steps.<step_id>.outputs`.

Resolution is delegated to `WorkflowEngine.WorkflowCallResolver` (`DefaultWorkflowCallResolver` by default), so applications can add their own `ref.kind` values without changing the `workflow.call` step shape.

#### Canonical call

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

#### Input/output contract

`workflow.call` acts like a function call between workflows:

| Where | Meaning |
|---|---|
| Parent workflow `data.inputs.*` | Inputs received by the currently running workflow. In CLI/Agent usage, these are the values passed by the caller or collected by the UI. |
| `workflow.call.input.args.*` | Values sent to the called workflow. |
| Called workflow `data.inputs.*` | The called workflow reads `args` here. |
| Called workflow `outputs.*` | Values returned by the called workflow. |
| Parent workflow `data.steps.<call_step_id>.outputs.*` | Returned values available after the call. |
| Parent workflow `data.steps.<call_step_id>.workflow` | Name of the workflow that was executed. |

If the called workflow has no `outputs` block, the engine returns the called workflow step outputs instead. Prefer defining explicit `outputs` so the contract stays stable.

Before executing the called workflow, the runtime applies defaults declared by its `inputs` schema and validates all resolved arguments. Missing required values or type mismatches fail immediately with `INPUT_VALIDATION` and identify the called workflow.

#### Function scope

`workflow.call` executes the called workflow with its own function scope:

- Document-level `functions:` are available to every workflow in the document.
- Workflow-level `functions:` are available only while that workflow is executing.
- A workflow-level function with the same name as a document-level function shadows it for that workflow only.
- Parent workflow-local functions do not leak into the called workflow, and called workflow-local functions do not leak back into the parent. Pass values through `input.args` and `outputs` instead.

```yaml
version: 1
name: workflow-call-function-scope
functions: |
  function label() { return "document"; }

workflows:
  main:
    functions: |
      function label() { return "main"; }
    steps:
      - id: before
        type: set
        input:
          value: "${functions.label()}"

      - id: call_helper
        type: workflow.call
        input:
          ref: { kind: local, name: helper }
          args: {}

      - id: after
        type: set
        input:
          value: "${functions.label()}"
    outputs:
      before: "${data.steps.before.value}"              # "main"
      helper: "${data.steps.call_helper.outputs.value}" # "helper"
      after: "${data.steps.after.value}"                # "main"

  helper:
    functions: |
      function label() { return "helper"; }
    steps:
      - id: local
        type: set
        input:
          value: "${functions.label()}"
    outputs:
      value: "${data.steps.local.value}"
```

If `helper` tried to call a function defined only under `main.functions`, dry-run and runtime execution would fail with an expression error. This keeps sub-workflows independently testable: every helper they need must come from document-level `functions:`, their own workflow-level `functions:`, or host-registered `WorkflowEngine.ScriptFunctions`.

#### Complete local example

This example defines three workflows in the same file:

- `main` receives the application input.
- `normalize_message` prepares data.
- `classify_message` consumes normalized data and returns a classification.

```yaml
version: 1
name: workflow-call-demo

workflows:
  main:
    inputs:
      message: { type: string, required: true }
    steps:
      - id: normalize
        type: workflow.call
        input:
          ref:
            kind: local
            name: normalize_message
          args:
            text: "${data.inputs.message}"

      - id: classify
        type: workflow.call
        input:
          ref:
            kind: local
            name: classify_message
          args:
            text: "${data.steps.normalize.outputs.normalized_text}"

      - id: summary
        type: template.render
        input:
          engine: mustache
          template: "Message '{{text}}' was classified as {{category}}."
          mode: text
          data:
            text: "${data.steps.normalize.outputs.normalized_text}"
            category: "${data.steps.classify.outputs.category}"

    outputs:
      normalized_text: "${data.steps.normalize.outputs.normalized_text}"
      category: "${data.steps.classify.outputs.category}"
      summary: "${data.steps.summary.text}"

  normalize_message:
    inputs:
      text: { type: string, required: true }
    steps:
      - id: normalize
        type: set
        input:
          normalized_text: "${lower(trim(data.inputs.text))}"
    outputs:
      normalized_text: "${data.steps.normalize.normalized_text}"

  classify_message:
    inputs:
      text: { type: string, required: true }
    steps:
      - id: classify
        type: set
        input:
          category: "${contains(data.inputs.text, 'urgent') ? 'critical' : 'standard'}"
    outputs:
      category: "${data.steps.classify.category}"
```

Run it from the CLI:

```bash
dotnet run --project src/GnOuGo.Flow.Cli -- run workflow-call-demo.yaml -i 'message=Urgent: please review this document'
```

Expected output fields:

```json
{
  "normalized_text": "urgent: please review this document",
  "category": "critical",
  "summary": "Message 'urgent: please review this document' was classified as critical."
}
```

#### Plugging into the current system

In the current GnOuGo flow system, the outer workflow is the integration point:

1. The CLI, Agent UI, API, or another workflow provides the outer workflow inputs.
2. The outer workflow maps those inputs into sub-workflow `args`.
3. Each sub-workflow declares the `inputs` it expects and the `outputs` it returns.
4. The outer workflow reads sub-workflow results from `data.steps.<call_id>.outputs`.
5. The outer workflow exposes its final contract through its own `outputs` block.

This keeps sub-workflows independently testable and reusable: a sub-workflow should not depend on the parent workflow's `data.inputs`; it should only depend on the `args` passed to it.

Use this same shape for every resolver-supported reference. The built-in resolver supports `local`, `url`, and `workspace` references, but documentation and generated workflows should prefer the local form above unless an application explicitly configures external workflow resolution.

---

### `workflow.route` — Route to Workflow Candidates

Selects one or more workflow candidates, executes them, and returns either raw results, the first answer, or an LLM-synthesized answer.

Candidates can mix explicit references and dynamic sources. A host supplies dynamic candidates through `WorkflowEngine.WorkflowCandidateProvider`; for example, `ref: { kind: database }` can expand to all persisted agent workflows in an application.

```yaml
- id: route
  type: workflow.route
  input:
    prompt: "${data.inputs.prompt}"
    history: "${data.inputs.history}"
    candidates:
      - ref: { kind: database, agent: DocumentAgent }
        description: Answers questions over local documents.
        tags: [documents, rag]
      - ref: { kind: database }
        tags_any: [git, documents]
        limit: 20
      - ref: { kind: local, name: fallback_general }
        description: General-purpose fallback.
    selection:
      mode: multiple
      min: 1
      max: 3
    args:
      passthrough: true
      auto_extract:
        provider: openai   # optional; omit to use runtime default
        model: gpt-5.4-mini
      add:
        history: "${data.inputs.history}"
    execution:
      parallel: true
      max_concurrency: 3
    combine:
      strategy: synthesize
```

Output shape:

```json
{
  "selected": [{ "id": "database:DocumentAgent", "name": "DocumentAgent", "reason": "..." }],
  "results": [{ "workflow": "DocumentAgent", "success": true, "outputs": { "answer": "..." } }],
  "answer": "Final synthesized answer",
  "text": "Final synthesized answer"
}
```

`args.passthrough: true` forwards all current `data.inputs` to each selected workflow. Extra undeclared inputs are preserved by the runtime and only declared fields are validated by the called workflow.

`args.auto_extract` can be `true` or an object with optional `provider`, `model`, and `temperature`. When enabled, `workflow.route` uses the selected workflow's declared `inputs` plus candidate `skill.inputs` metadata to extract structured arguments from `prompt` and `history` before calling the workflow. If provider/model are omitted, the runtime defaults are used.

---

### `workflow.plan` — Generate a Workflow Dynamically via LLM

The most powerful step type: asks an LLM to **generate a complete YAML workflow** from a natural-language instruction, then validates and compiles it before execution.

`mode` defaults to `auto`. Auto mode first asks the configured LLM to estimate the request's cyclomatic complexity and choose `basic` or `pipeline`. It chooses `basic` for requests under 10 meaningful branches, and `pipeline` when the request should be decomposed into leaf workflows before assembly.

#### Basic usage

```yaml
- id: plan
  type: workflow.plan
  input:
    mode: auto                    # default; use basic to force the single-plan path
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
    mode: auto                    # auto | basic | pipeline
    generator:
      model: gpt-4o                 # LLM model for planning
      provider: openai              # Optional — LLM provider
      instruction: "Analyze the user's request and build a workflow."
      context: "${json(data.inputs)}"

      # Reasoning effort for the planning LLM call (and the MCP pre-filter).
      # Defaults to "medium" because planning is reasoning-heavy work.
      # Set to "auto" to let the provider decide, or any of:
      # "minimal" | "low" | "medium" | "high" | "max" | "auto".
      # Models without thinking support ignore this field.
      reasoning: medium

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
      mode: strict                  # Optional marker; strict validation is mandatory
      compile: true                 # Legacy field; compile/semantic validation is always forced
      dry_run: true                 # Execute once with deterministic fake providers
      repair: auto                  # Optional marker; bounded automatic repair is mandatory
      max_repair_attempts: 3        # Preferred repair attempt budget

    # Self-correction on failure
    on_invalid:
      action: reprompt              # Legacy field; invalid YAML is always reprompted while attempts remain
      max_attempts: 3               # Legacy repair attempt budget when validate.max_repair_attempts is absent
```

#### Auto and basic modes

`mode: auto` is the default. It performs one classifier LLM call before generation and returns the classifier result under `meta.mode_selection`. The classifier estimates complexity by counting meaningful branches such as conditions, switch/case paths, loops, retries, error handling, cleanup paths, validation branches, tool-orchestration choices, and state transitions.

Use `mode: basic` to skip classification and run the original single workflow-generation path directly. Use `mode: pipeline` to force decomposition.

#### Pipeline mode

Use `mode: pipeline` when the input is a raw user automation prompt that should be cleaned up, segmented into leaf subworkflows, and assembled into one local YAML document.

```yaml
- id: plan_pipeline
  type: workflow.plan
  input:
    mode: pipeline
    name: repository-issue-report
    skill:
      description: Build a report from repository issues.
      tags: [github, issues]
      inputs:
        target_repository_url:
          type: string
          required: false
          default: https://github.com/AxaFrance/oidc-client
        number_of_issues_to_process:
          type: number
          required: false
          default: 20
      outputs:
        report_path: string
    raw_prompt: "${data.inputs.prompt}"
    generator:
      model: gpt-4o
      provider: openai
      reasoning: medium
      prefilter: false
    validate:
      mode: strict
      dry_run: true
      max_repair_attempts: 3
    on_invalid:
      action: reprompt
      max_attempts: 3
```

Pipeline mode runs five traced phases:

1. `normalize_user_prompt` rewrites the raw prompt as clean Markdown without changing meaning.
2. `mark_extractable_blocks` wraps only significant algorithmic sections in `:::subworkflow name="..."` blocks and adds a `## Main workflow orchestration` section.
3. `extract_subworkflow_specs` parses those blocks as-is, builds generation prompts, and reports validation errors for nested blocks or subworkflow-call mentions.
4. `generate_subworkflows` runs the normal `workflow.plan` generator for each leaf workflow in parallel. Each leaf prompt contains only that leaf's goal, input/output contract, and content; leaf generation forbids `workflow.call` and `workflow.plan`, preserves the configured MCP prefilter behavior, forces validation, retries failed leaf generation up to the parent repair attempt budget, and rejects bare `type: object` schemas unless they define non-empty `properties`.
5. `assemble_main_workflow` sends a compact leaf manifest, the generated leaf contracts, and a minimal main-graph DSL context to the LLM. The LLM returns only a `document` plus orchestration `graph`; the runtime renders the real `main` workflow deterministically and grafts the validated leaf workflows before final validation.

The final YAML has exactly one hierarchy level: `main` may call local leaf workflows with `workflow.call`, while leaf workflows must never contain `workflow.call` or `workflow.plan`. The returned `pipeline` object includes `normalized_markdown`, `annotated_markdown`, and parsed `specs`.

When a generated leaf workflow contains root-level helper functions, final assembly moves those helpers into the grafted leaf workflow's own `functions:` block. They are not promoted to the final document root, so helpers remain isolated with the leaf that uses them.

Standalone generated leaf:

```yaml
version: 1
name: parse-repository
functions: |
  function parseRepoUrl(url) {
    var parts = url.replace(/\/$/, "").split("/");
    return {
      owner: parts[parts.length - 2],
      repo: parts[parts.length - 1]
    };
  }
workflows:
  parse_repository:
    inputs:
      repo_url: { type: string, required: true }
    steps:
      - id: parsed
        type: set
        input:
          value: "${functions.parseRepoUrl(data.inputs.repo_url)}"
    outputs:
      owner: "${data.steps.parsed.value.owner}"
      repo: "${data.steps.parsed.value.repo}"
```

Composed pipeline document:

```yaml
version: 1
name: repository-pipeline
workflows:
  main:
    inputs:
      repo_url: { type: string, required: true }
    steps:
      - id: parse
        type: workflow.call
        input:
          ref: { kind: local, name: parse_repository }
          args:
            repo_url: "${data.inputs.repo_url}"
    outputs:
      owner: "${data.steps.parse.outputs.owner}"
      repo: "${data.steps.parse.outputs.repo}"

  parse_repository:
    functions: |
      function parseRepoUrl(url) {
        var parts = url.replace(/\/$/, "").split("/");
        return {
          owner: parts[parts.length - 2],
          repo: parts[parts.length - 1]
        };
      }
    inputs:
      repo_url: { type: string, required: true }
    steps:
      - id: parsed
        type: set
        input:
          value: "${functions.parseRepoUrl(data.inputs.repo_url)}"
    outputs:
      owner: "${data.steps.parsed.value.owner}"
      repo: "${data.steps.parsed.value.repo}"
```

Configured `name`, `skill`, and public input schemas are authoritative and are preserved exactly in the root skill and `main` workflow. Leaf inputs are call arguments and are not automatically promoted to public inputs; the main assembler maps public names to leaf argument names and derives internal values in workflow steps. When no structured contract is configured, the final assembly phase infers the public contract from the normalized user request, but leaf call arguments and available outputs come from the actual generated leaf workflows rather than the initial extraction draft. Composition rejects any `data.inputs.<name>` reference that is not declared by the resolved main input contract, and it also rejects calls that omit required arguments from the generated leaf contract.

`validate.max_repair_attempts` controls the bounded automatic repair budget. When it is absent, `on_invalid.max_attempts` is used for compatibility, then the default is 3. The budget is applied to extractable-block annotation repair, each leaf generation, and the final main-workflow assembly. If block extraction validation fails, the next `mark_extractable_blocks` attempt receives the previous annotated Markdown plus exact validation feedback. If final parsing, policy, hierarchy, compilation, or semantic validation fails, the next assembly attempt receives the previous YAML response and structured validation error so it can repair the complete `document` and `graph` mapping.

The final composed pipeline document uses the same validation sequence as standard `workflow.plan`: policy and limits are enforced, compiler validation and MCP-contract-aware semantic validation are always forced, and `validate.dry_run` executes the complete entrypoint with deterministic fake LLM, MCP, and human-input providers. `validate.compile: false` is accepted only as a legacy no-op and cannot disable strict validation. MCP discovery contracts are collected once for final validation, and every assembly attempt emits its own `workflow.plan.validate` telemetry span.

**Output:** `{ workflow: { version, name, workflows: [...] }, yaml: "...", meta: { model, attempt?, mode, mode_selection? }, diagnostics: [...], pipeline? }`

**Features:**

- **Automatic MCP discovery**: Connects to all configured MCP servers, lists their tools/prompts, and injects them into the planning prompt so the LLM knows what's available. A transient discovery failure is retried up to three total attempts with progressive 500 ms and 1,000 ms delays.
- **MCP pre-filter**: Uses a lightweight LLM call to select only the MCP servers/tools relevant to the task instruction — reduces prompt size and cost.
- **Full DSL reference injection**: The LLM receives the complete DSL documentation (step types, expressions, error handling) so it can generate valid workflows.
- **Policy enforcement**: Generated workflows are validated against allowed/denied step types and max step limits.
- **Mandatory strict validation before acceptance**: `workflow.plan` always runs the validator, compiler, and semantic checks before returning a plan. `validate.compile: false` is tolerated for older workflows but is ignored. This catches non-fatal validator diagnostics such as unknown step types, invalid container shapes, unknown YAML structural keys, future step references, conditional branch/loop mapping errors, and invalid `data.steps.<id>.response.<field>` mappings.
- **Structured repair diagnostics**: Validation and `dry_run` failures include machine-readable `details.diagnostics[]` entries with stable codes, locations, hints, expected shapes, allowed paths when available, and `llm_guidance` for reprompt repair.
- **Executor-owned step contracts**: Every registered executor must expose declarative JSON Schema input/output contracts. Planning validation recursively rejects missing required fields, wrong literal types, unknown keys (including nested keys), and mutually exclusive fields. A custom registered executor without a contract fails closed.
- **Static expression type inference**: Exact `${...}` references inherit types from workflow inputs, previous step outputs, `set.output_schema`, and scoped loop variables; embedded interpolation is a string, and built-ins such as `len`, `toNumber`, `exists`, `json`, `pick`, and `omit` have known result types. Incompatible assignments such as `llm.call.text` into `max_tokens` or `data.<item_var>.title` into an integer workflow-call input fail with `EXPR_TYPE_MISMATCH` before dry-run. Opaque/custom expressions remain runtime-validated.
- **Local workflow-call contracts**: Literal local `workflow.call` targets are validated against the called workflow's declared inputs. Missing, extra, and wrongly typed `input.args` fail during plan validation, and the called workflow's typed outputs are propagated so invented paths like `data.steps.call.outputs.unknown` are rejected.
- **Optional dry-run validation**: Set `validate.dry_run: true` to execute the generated workflow once with deterministic fake LLM, MCP, human-input, and routing providers. This catches runtime input-resolution errors such as free-form `llm.call.text` being used where a number is required. Dry-run MCP sessions expose only discovered tools: an invented method fails instead of receiving a generic mock response. The dry-run never calls real LLMs or MCP tools.
- **MCP output contracts**: MCP discovery injects complete `input_schema`, `output_schema`, and `example_response` metadata into the planning prompt. `output_schema` / `example_response` define which fields may be read from `mcp.call` single-tool `response` objects.
- **Fail-closed MCP discovery**: A generated tool-mode `mcp.call` is rejected when its server catalog is missing, discovery failed after all retries, or the discovered catalog is empty. Dynamic server names cannot pass plan validation because their existence cannot be proven.
- **MCP envelope, target, and request validation**: Unknown fields at `mcp.call.input` are rejected with a suggestion to move tool arguments under `input.request`. Literal `method` and every literal entry in `methods` must exist in the discovered server contract. The shared request is validated against every selected tool schema.
- **Expanded JSON Schema checks**: MCP requests support `enum`, `const`, `allOf`, exact `oneOf`, `anyOf`, string length/pattern, numeric bounds/multiples, object/array size, `uniqueItems`, nested schemas, and schema-correct `additionalProperties` behavior. Quoted numeric, integer, and boolean YAML scalars remain strings during `workflow.plan` validation and fail when the contract requires real booleans or numbers.
- **Runtime request validation**: Requests containing expressions or `request_template` are checked again after resolution/rendering and immediately before `CallToolAsync`. The live tool catalog proves the method exists, and the resolved request must satisfy that tool's `input_schema`.
- **Bounded automatic self-correction**: If the generated YAML is invalid (parse error, policy violation, compilation error, semantic mapping error, or optional dry-run failure), the structured error is sent back to the LLM for repair until `validate.max_repair_attempts`, legacy `on_invalid.max_attempts`, or the default budget is exhausted. `on_invalid.action` is accepted only for compatibility and cannot disable automatic repair while attempts remain.
- **OpenTelemetry tracing**: Full GenAI convention traces for the planning LLM call, MCP discovery, and pre-filter phases.

Workflow execution traces also include injected workflow inputs on the workflow span:

- `gnougo-flow.workflow.inputs` as a single JSON string with secret-looking keys such as `token`, `password`, `secret`, and `api_key` redacted.
- `gnougo-flow.workflow.inputs.count`
- `gnougo-flow.workflow.inputs.keys`

**Semantic mapping guardrails:** generated plans must not read `data.steps.<id>.*` from steps that are produced only inside a `switch` case, an `if`-guarded step, or a loop body unless that value is first mapped into a guaranteed location. Function arguments are evaluated eagerly, so `coalesce(data.steps.fix.value, data.steps.question.value)` is still unsafe when either step may not have executed. Prefer a common workflow-level output alias in every branch, or a guaranteed normalization step with a stable output schema.

Loop outputs need special care: `data.steps.<loop_id>.results` is an array of per-iteration `data.steps` snapshots, not an array of the last child step's output. If a loop child `set` step named `build_issue_result` produces `handled_by_gnougo`, post-loop code must read `iteration.build_issue_result.handled_by_gnougo`. To produce a flat list, create a typed child `set` step in the loop and flatten/filter through that child step id.

Generated YAML should preserve typed scalars: emit `required: false`, `strict: true`, `timeout_ms: 1200000`, `perPage: 30`, and `append: false` as booleans/numbers, not quoted strings. Use literal block scalars (`|`) for multiline prompts/templates or strings containing JSON/double quotes. For GitHub issue workflows, derive `owner` and `repo` once from `repository_url`, preserve `body` in normalized issue objects, and never pass empty strings for required GitHub action fields that can be derived or normalized.

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

Expressions are embedded in strings using `${...}` syntax. They are JavaScript expressions evaluated by the Jint engine.

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
| `string(val)` | Converts value to string |
| `toString(val)` | Alias for `string(val)` |
| `toNumber(val)` | Converts to number |
| `json(val)` | Serializes value to JSON string |
| `pick(obj, ...keys)` | Returns a new object containing only the requested keys; keys may be separate arguments or an array |
| `omit(obj, ...keys)` | Returns a new object with the requested keys removed; keys may be separate arguments or an array |
| `fromJson(s)` | Parses a JSON string into a node |
| `now()` | Returns the current local date/time as an ISO-8601 string |
| `base64(val)` | Encodes the UTF-8 string value as Base64 |
| `formatDate(dateStr, fmt)` | Formats a date string (default: `yyyy-MM-dd`) |

### Full JavaScript support

- Ternary: `${data.inputs.mode == "fast" ? 0.0 : 0.7}`
- Template literals: `` ${`Hello ${data.inputs.name}`} ``
- Array methods: `${data.inputs.items.filter(i => i.active).length}`

### Runtime limits

Expression evaluation is sandboxed through `ExecutionLimits`:

| Property | Default | Description |
|----------|---------|-------------|
| `MaxExpressionAstNodes` | `500` | Parser/validator complexity limit. |
| `MaxExpressionStatements` | `100000` | Jint statement budget for expression evaluation. |
| `ExpressionTimeoutSeconds` | `15` | Evaluation timeout. |
| `ExpressionMemoryLimitBytes` | `50000000` | Jint memory limit. |

Increase these limits only for trusted workflows; prefer simplifying expressions or moving complex logic to WFScript functions.

---

## WFScript — Custom JavaScript Functions

Define reusable functions in the `functions:` block (document-level or workflow-level).
When `workflow.plan` generates custom functions, each generated `function` must be immediately preceded by JSDoc with typed `@param` entries for every parameter and a typed `@returns` entry for the output:

Scope rules:

- Document-level `functions:` are loaded for every workflow in the document.
- Workflow-level `functions:` are loaded only for the workflow currently being executed.
- Workflow-level functions shadow document-level functions with the same name for that workflow only.
- `workflow.call` and `workflow.execute` create an isolated execution scope for the called or executed workflow.
- Host-registered `WorkflowEngine.ScriptFunctions` are added after YAML functions and can override YAML helpers when the host intentionally provides a function with the same name.
- Expressions can call helpers as either `functions.name(...)` or `name(...)`; the `functions.name(...)` form is preferred in workflow YAML because it makes helper calls explicit.

```yaml
version: 1
name: smart-triage
functions: |
  /**
   * Classifies a message by urgency and issue type.
   *
   * @param {string} text - Message text to classify.
   * @returns {string} Routing label: "critical", "bug", or "general".
   */
  function classify(text) {
    if (contains(lower(text), "urgent")) return "critical";
    if (contains(lower(text), "bug")) return "bug";
    return "general";
  }

  /**
   * Truncates text to a maximum visible length.
   *
   * @param {string} text - Text to truncate.
   * @param {number} maxLen - Maximum number of characters.
   * @returns {string} Original or truncated text.
   */
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
                  mode: text
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

```bash
# Validate a workflow (check syntax, types, compilation)
dotnet run --project src/GnOuGo.Flow.Cli -- validate examples/triage.yaml

# Inspect the structure (workflows, steps, inputs, outputs)
dotnet run --project src/GnOuGo.Flow.Cli -- inspect examples/triage.yaml

# Execute with key=value inputs
dotnet run --project src/GnOuGo.Flow.Cli -- run examples/triage.yaml -i 'message=hello' -i 'priority=normal'

# Execute with full JSON input
dotnet run --project src/GnOuGo.Flow.Cli -- run examples/triage.yaml -j '{"message":"hello","priority":"normal"}'
```

---

## NativeAOT

The engine is fully **NativeAOT**-compatible:

- `GnOuGo.Flow.Core`: `IsAotCompatible=true`
- `GnOuGo.Flow.Cli`: `PublishAot=true`
- YAML: YamlDotNet RepresentationModel (DOM, no reflection)
- JSON: `System.Text.Json.Nodes.JsonNode` everywhere (no reflection-based serialization)
- Templating: Manually implemented Mustache (no external library)
- Scripting: Jint v4+ (pure interpreter, no Reflection.Emit)
