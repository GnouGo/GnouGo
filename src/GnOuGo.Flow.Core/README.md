# GnOuGo.Flow — YAML Workflow DSL Engine

<a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/v/GnOuGo.Flow.Core.svg" alt="NuGet version"></a>
<a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt=".NET 10.0"></a>
<a href="https://nugettrends.com/packages?ids=GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/dt/GnOuGo.Flow.Core.svg" alt="NuGet downloads"></a>

Declarative workflow engine based on a YAML DSL, **NativeAOT**-compatible (.NET 10).
Write YAML workflows that orchestrate LLMs, MCP servers, templates, loops, human input, and dynamic code generation — all from a single file.

## MCP protocol compatibility

Flow.Core owns only provider-neutral MCP contracts and has no dependency on the MCP SDK or another GnOuGo package. `GnOuGo.Flow.Integrations` supplies the stable C# MCP SDK `2.2.0` HTTP/stdio implementation, which prefers MCP `2026-07-28` discovery with `server/discover` and automatically falls back to `2025-11-25` initialization. Flow does not use `Mcp-Session-Id` for Copilot identity.

Every discovery/tool/resource/prompt request carries reserved technical metadata such as correlation, stable execution and agent identity, run, trace, step, and tenant identifiers under `_meta.gnougo`; HTTP headers and stdio environment receive the same technical identifiers. These host-owned fields cannot be overridden by workflow input. A caller may explicitly add domain-neutral request context through `mcp.call.input.context`, which is propagated only under `_meta.gnougo.context`. Flow never extracts domain fields from workflow data. MCP elicitation is bridged to the workflow `IHumanInputProvider`, enabling stable multi-round-trip HITL without putting credentials in YAML. MCP tools marked `gnougo.management.visibility=management_only` remain discoverable to management clients but are excluded from workflow planning catalogs.

Before the first tool call on each live MCP client, Flow performs that client's
`tools/list` discovery even when the process-wide capability catalog is already
cached. This lets the SDK register transport annotations such as `x-mcp-header`
and reliably emit their `Mcp-Param-*` headers; a catalog learned by an older
client is used for validation, but never substitutes for live-session setup.

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
  - [decision.evaluate](#decisionevaluate--finite-runtime-decisions)
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
  GnOuGo.Flow.Integrations/  # AI provider + MCP transport adapters
  GnOuGo.Flow.Cli/           # CLI (validate, run, inspect)
    examples/             # YAML examples
  GnOuGo.Flow.Server/        # HTTP API + React/Vite front-end
tests/
  GnOuGo.Flow.Tests/         # Unit tests
  GnOuGo.Flow.Integrations.Tests/ # Integration adapter tests
```

Flow.Core never references another `GnOuGo.*` project or package. Hosts inject
`ILLMClient`, `IMcpClientFactory`, `IMcpExecutionHooks`, and
`IModelUsageCostEstimator` implementations. Install `GnOuGo.Flow.Integrations`
when using the built-in GnOuGo AI routing or MCP transports.

Flow keeps one internal type representation for validation: `FlowTypeDescriptor`.
Workflow `InputDef`/`OutputDef`, executor `StepContract` schemas, MCP JSON Schema, and workflow.plan contract snippets are converted into or out of this descriptor instead of being reasoned about as separate type systems.

String workflow inputs and outputs may declare `enum: [value_a, value_b]`. Values must be non-empty and unique, and `enum` is valid only with `type: string`. The constraint is preserved through JSON Schema conversion and local `workflow.call` compatibility, enforced at runtime, and included in generated contracts. Existing unconstrained string contracts remain valid.

During workflow.plan semantic validation, a `WorkflowSymbolTable` is built as steps are walked. It tracks workflow inputs, scoped data variables, available step output types, and control-flow availability so expressions such as `data.steps.<id>.<field>` and loop-local `data.<item_var>.<field>` can be checked against known symbols before generated YAML is accepted.

Step outputs are resolved through `StepOutputTypeResolver`: each step starts from its executor contract and can be refined by static input, such as `set.output_schema`, `llm.call.input.structured_output`, validated protocol-declared MCP tool output schemas, local `workflow.call` targets, `template.render` mode, `human.input` form fields, and loop body output snapshots.

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

Applications that use the built-in AI routing and MCP SDK transports should also install the independently publishable integration package:

```bash
dotnet add package GnOuGo.Flow.Integrations
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
    finally:                  # Optional cleanup after success, failure, or cancellation
      - id: release_resources
        type: emit
        input: { message: "Releasing resources", level: progress }
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
| `data.workflow_error` | Finalizers only: null after success, otherwise the primary workflow error |

---

### Workflow Finalization

An optional workflow-level `finally` array contains ordinary Flow steps that execute once after the main `steps`, including after failure or caller cancellation. Finalizers retain access to inputs and completed step outputs and run with an independent token. The defaults are a 30-second timeout and 50 finalization steps. A finalizer failure fails an otherwise successful workflow; if the main workflow already failed, its error remains primary and `details.finalization_errors` records cleanup failures. Use idempotent operations because process termination still relies on component TTL cleanup.

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
    servers: [inventory, docs]      # Required — configured MCP server names
    include: ["tools", "prompts"] # Optional — default: ["tools"]

- id: discover_all
  type: mcp.list
  input:
    servers: ["*"]
    include: ["tools"]
```

**Output:** `{ status, text, servers: [...], tools: [...], resources: [...], prompts: [...] }`

Flattened `tools`, `resources`, and `prompts` entries each include a `server` field so downstream steps can keep the server affinity when multiple MCP servers are discovered at once. Tool entries also include `output_contract` with `schema`, `source`, `authoritative`, and bounded validation `errors`. The compatibility `output_schema` field remains available.

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

An optional context object can carry non-secret application metadata to a specialized MCP boundary:

```yaml
- id: reserve_stock
  type: mcp.call
  input:
    server: inventory
    kind: tool
    method: reserve_items
    context:
      workspace: "${data.inputs.workspace}"
      operation_revision: "${data.inputs.revision}"
    request:
      items: "${data.inputs.items}"
```

`context` is copied only to `_meta.gnougo.context`. It does not create dedicated HTTP headers or stdio environment variables. Reserved technical keys and keys representing secrets, tokens, passwords, credentials, API keys, or authorization are rejected recursively.

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
    servers: [inventory]

- id: smart_call
  type: mcp.call
  input:
    server: inventory
    model: gpt-4o-mini
    temperature: 0.2
    prompt: "Find and call the right tool to list available items"
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

> **Important:** The `response` object is tool-specific. `workflow.plan` treats single-tool MCP responses as opaque unless the tool advertises a valid protocol `ReturnJsonSchema`, exposed through the compatibility `OutputSchema` property. Access `data.steps.<id>.response.<field>` only when that authoritative schema declares the field. Otherwise pass the whole response with `json(data.steps.<id>.response)` or add an `llm.call`/`mcp.call` normalization step with strict `structured_output`.
>
> When an MCP server returns protocol `structuredContent`, `mcp.call` uses that value as `response`. `McpOutputContractResolution` records the discovered schema provenance as `protocol_schema`, `example`, or `description`. Only an error-free `protocol_schema` resolution is authoritative. Example- and description-derived shapes remain prompt hints and never prove nested response fields or capability data flow.

Resolved request properties whose discovered input schema marks them optional are omitted when their value is JSON `null`. This lets one typed request represent optional scalar fields without sending schema-invalid nulls. A null value for a required property is never omitted and still fails before transport.

Documented action selectors (`method`, `action`, `operation`, `command`, `mode`, `event`, `kind`, JSON Schema `const`, and explicit discriminators) must be literal request scalars. Generated expressions cannot hide or dynamically replace the logical MCP operation selected during planning.

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

#### MCP elicitation → visible Human Input

An MCP form-elicitation request is surfaced through the same `HumanInputRequest` contract as a `human.input` step. `mcp.call` emits `gnougo-flow.step.waiting_for_human` before awaiting the provider, then `gnougo-flow.step.human_input_resumed` with a `resumed`, `refused`, or `cancelled` phase. Correlation metadata sent back by the MCP server identifies the exact run and step, including when a transport client is cached or several calls use the same server and method concurrently. An external server that omits this metadata can use the sole active call for that server; an ambiguous concurrent request is rejected instead of risking cross-run input delivery. Caller cancellation remains workflow cancellation and releases the pending provider request; only expiration of the dedicated MCP timeout is reported as `MCP_TIMEOUT`.

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

Generated `set.output_schema` values use JSON Schema. During plan normalization, workflow-contract shorthand such as `dictionary`, `required_properties`, and `additional_properties` is converted to the corresponding JSON Schema object form. Concrete nullable unions remain intact because they are enforceable by the JSON Schema runtime.

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

#### Boolean confirmation

```yaml
- id: confirm_send
  type: human.input
  input:
    mode: confirm
    prompt: "Send the email now?"
    choices: [approve, reject]
- id: route_send
  type: switch
  cases:
    - when: "${data.steps.confirm_send.response}"
      steps:
        - { id: send, type: workflow.call, input: { ref: { kind: local, name: send_email } } }
```

`confirm` always exposes `response` as a Boolean. Providers may submit a Boolean,
a common label such as `approve`/`reject`, or one of two custom presentation
choices; the runtime normalizes the first choice to `true` and the second to
`false`. Branch on the Boolean directly rather than comparing it to a label.

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
        type: radio
        options: [us-east, eu-west, ap-south]
        option_definitions:
          - { value: us-east, description: "Lowest latency for the primary workload.", recommended: true }
          - { value: eu-west, description: "Keep processing in the European region.", recommended: false }
          - { value: ap-south, description: "Keep processing in the Asia-Pacific region.", recommended: false }
        allow_custom_answer: true
        default: us-east
      - name: max_retries
        type: string
        required: false
        default: "3"
```

Rich `option_definitions` preserve the legacy string `options` values while adding descriptions and one optional recommendation marker. `allow_custom_answer: true` asks compatible hosts to render a native Other control. Set form-level `allow_abandon: true` to expose an explicit exit; providers then return `{ "_action": "abandon" }`. Successful rich hosts include `_action: submit`, while existing provider responses without `_action` remain valid.

**Output:** The user's response as a JSON object (e.g., `{ "response": "approve" }` for `choice`, `{ "response": true }` for `confirm`, or `{ "api_key": "...", "region": "eu-west", "max_retries": "3" }` for `form`).

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

### `decision.evaluate` — Finite Runtime Decisions

Use `decision.evaluate` when several runtime results must be reduced to one or more finite decisions before conditional effects execute. The step is provider-neutral and evaluates every field atomically.

```yaml
- id: compute_decisions
  type: decision.evaluate
  input:
    decisions:
      publication:
        allowed_values: [PUBLISH_A, PUBLISH_B, NO_EFFECT]
        cases:
          - when: "${data.steps.first.is_valid}"
            value: PUBLISH_A
          - when: "${data.steps.second.needs_attention}"
            value: PUBLISH_B
        default: NO_EFFECT
```

`allowed_values` and case values must be non-empty unique strings; every case value and optional default must be allowed. Each `when` must resolve to a boolean. More than one matching case, or no match without a default, fails closed with non-retryable `DECISION_EVALUATION_UNRESOLVED`. Malformed or over-limit contracts use `INPUT_VALIDATION`. Decision and per-field case counts are bounded by `ExecutionLimits.MaxSwitchCases`. If any field fails, no partial output is exposed.

Output is the selected field map, for example `{ "publication": "PUBLISH_A" }`.

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
      human_input:
        enabled: true      # optional; false by default
        timeout_ms: 36000000
        max_attempts: 3
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

`args.auto_extract` can be `true` or an object with optional `provider`, `model`, and `temperature`. When enabled, `workflow.route` resolves the selected workflow, treats that workflow's declared YAML `inputs` as the authoritative target contract, and asks the LLM to map `prompt` and `history` into exactly those input names. Candidate `skill.inputs` metadata is included only as a hint. Extracted fields and passthrough aliases that are not declared by the target workflow input schema are ignored. After merging extracted values with matching passthrough/additional args, defaults are applied and the selected workflow inputs are validated before execution. If provider/model are omitted, the runtime defaults are used.

`args.human_input` can be `true` or an object with `enabled`, `timeout_ms`, and `max_attempts`. It is disabled by default. When enabled and a selected workflow still has missing or invalid declared inputs after auto-extraction and defaults, the router asks the configured `IHumanInputProvider` for only those fields, converts responses to their declared types, and validates again before execution. Multiple selected workflows collect their forms one at a time so UI providers with a single active Human Input panel are not overwritten; completed workflows still use the configured execution parallelism. Complex array/object/dictionary inputs are requested as JSON. `timeout_ms` defaults to 36,000,000 (10 hours), `0` disables the timeout, and `max_attempts` defaults to `3`.

If interactive completion is enabled but no provider is configured, the route fails with `NO_HITL_PROVIDER`. A request timeout returns `HUMAN_INPUT_TIMEOUT`; exhausting all attempts returns the normal routed `INPUT_VALIDATION` details.

Before each selected workflow runs, `workflow.route` emits a `gnougo-flow.step.thinking` event with level `progress`, source `workflow.route`, selected workflow metadata, and routed input keys. When `ExecutionLimits.LogStepContent` is enabled, the message also includes redacted/truncated resolved inputs using the same telemetry redaction as workflow input logging.

---

### `workflow.plan` — Generate a Workflow Dynamically via LLM

The most powerful step type: asks an LLM to **generate a complete YAML workflow** from a natural-language instruction, then validates and compiles it before execution.

`mode` defaults to `auto`. Auto mode first asks the configured LLM to estimate the request's cyclomatic complexity and choose `basic` or `pipeline`. It chooses `basic` for requests under 10 meaningful branches, and `pipeline` when the request should be decomposed into leaf workflows before assembly.

Every internal planning call is background-capable: automatic mode classification, capability
inventory and repair, physical candidate selection, capability matching and repair, MCP
prefiltering, pipeline stages, and final generation. With OpenAI this uses background Responses
and preserves strict structured-output schemas. Ordinary `llm.call`, chat, and tool-calling
requests keep their existing routing unless their caller explicitly opts into background mode.

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
    mode: auto                    # auto | basic | pipeline | repair
    raw_prompt: "${data.inputs.request}"
    intent_clarification:
      mode: always                # off (default) | when_needed | always
      timeout_ms: 36000000
      max_rounds: 2
      max_questions: 8
      max_questions_per_round: 5
    llm_budget:                   # Optional; absent preserves legacy behavior
      max_calls: 40
      max_total_tokens: 1500000
      max_elapsed_ms: 1800000
      max_estimated_cost:         # Optional; requires usage, pricing, and conversion metadata
        amount: 50
        currency: EUR
      unverifiable: fail
    capability_preflight:
      mode: infer                 # off (default) | infer | explicit
      clarification:
        enabled: true             # opt-in; default false
        timeout_ms: 36000000      # one batched human clarification form
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
        - decision.evaluate
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

#### Generic intent clarification

`intent_clarification` is disabled by default. `always` requires an up-front form before discovery or generation unless the structured analyst classifies the request as intrinsically contradictory, unsafe, or impossible to clarify. `when_needed` first permits the analyst to classify an already decision-complete request as `sufficient` without displaying a form.

Each form contains one to `max_questions_per_round` single-choice questions, with two or three mutually exclusive described options. The AI recommendation is first, marked recommended, and preselected, but the user must submit explicitly. Rich hosts add a custom Other answer and an Abandon action. Generated question content follows the raw request language; fixed controls are localized by the host and fall back to English.

The round and question limits are shared by the complete `workflow.plan` run. Remaining rounds may be used for genuine intent ambiguity discovered after bounded capability or extraction repair. After automatic matching repair is exhausted, one behavior-only form may also offer explicit read-only continuation, but only when every blocker is a conditional external write and a validated read/execution result remains safe. Its recommended answer preserves the requested writes and stops; accepting read-only omits only those writes and restarts complete preflight once. It never offers unconditional writes, a generation-time fixed decision, removal of required cleanup, or weakening of required reads/execution. Runtime-dependent outcomes remain conditional workflow branches and are never sent to the human for prediction.

Malformed model contracts, invalid catalog IDs, unavailable reads/execution, schema violations, lifecycle gaps, mixed blockers, and ordinary generation defects are not clarification-eligible. They retain their normal fail-closed errors. Intent clarification uses `WORKFLOW_PLAN_CLARIFICATION_FAILED` for provider, timeout, response, or analyst-contract failures; `WORKFLOW_PLAN_CANNOT_PLAN_SAFELY` for intrinsic or budget-exhausted ambiguity; and `WORKFLOW_PLAN_ABORTED` for explicit abandonment. Submitted form and question counters are refreshed immediately and included with later terminal matching failures. Failure metadata contains only stage, classification, counts, reason, and recommended action—not submitted answers.

#### Provider-neutral LLM usage budgets

`llm_budget` bounds every LLM call made by the `workflow.plan` step, including clarification, discovery filtering, capability inference, extraction, quality review, generation, and repair. The contract is disabled when absent. Configured call and elapsed limits are checked before dispatch; token and estimated-cost totals are recorded from provider-neutral response usage before a response can be accepted. `max_estimated_cost` accepts a positive decimal amount and an uppercase three-letter currency. The legacy `max_estimated_cost_usd` field remains supported as a USD alias, but the canonical and legacy limits cannot be set together. Missing usage, pricing, pricing currency, or a required exchange quote fails closed with `LLM_BUDGET_UNVERIFIABLE`.

Hosts may attach a parent `LLMUsageBudgetScope` to `WorkflowEngine.LLMUsageBudget`. A plan scope becomes its child, so it can tighten but never loosen the host allowance; routed child workflows inherit the same parent. `IModelUsageCostEstimator.EstimateCostWithCurrency` reports the pricing-metadata currency; existing estimators remain compatible through the default USD adapter. `IExchangeRateProvider` supplies provider-neutral conversion quotes. A scope pins the first validated quote for each currency pair in its durable snapshot and reuses it for every later call. Monetary scopes serialize unaccounted calls, limiting local estimation overshoot to one in-flight request. A provider-side hard spend limit is still required when an exact monetary ceiling matters.

Budget snapshots contain call counts, token totals, normalized estimated cost and currency, pinned quote metadata, and the legacy USD total. `gnougo-flow.llm_budget.updated` telemetry emits those cumulative counters plus elapsed time, stage, status, and a random call identifier, without serializing the quote list. Neither contract contains prompts, responses, credentials, provider bodies, or human answers.

#### Generic capability preflight

`capability_preflight.mode: infer` discovers every configured MCP catalog and starts by inventorying positive runtime operations and constraints without exposing tools. When `generator.prefilter` is enabled (the default), Flow then pages through a compact one-entry-per-physical-tool catalog to select relevant candidates, adds compatible MCP-declared artifact producers, and only then builds the schema-aware matching catalog. Enum, `const`, nested selector, discriminator, `oneOf`, and `anyOf` variants reference their base physical contract and carry only their exact request bindings. Required unavailable operations fail before classification, decomposition, or YAML generation. Prohibitions, safety rules, ordering requirements, and invariants are constraints rather than executable operations, so abstaining never requires a tool.

The inventory excludes configuration already supplied by the host, provider or credential resolution performed internally by a selected capability, and persistence performed outside the generated workflow. Inventory completeness means that all requested runtime intentions were enumerated; it does not assert that tools or selector matches exist. If the first inventory is incomplete or violates its structured evidence contract, Flow performs one bounded repair call. The repair receives the rejected bounded candidate and precise field-level issue codes. A second evidence-contract violation fails with `CAPABILITY_PREFLIGHT_INFERENCE_FAILED`, classification `model_contract_violation`, and a retry/change-model recommendation; it is not presented as user-intent ambiguity. Candidate selection likewise performs one bounded repair when a required external operation has no candidate after every compact page was considered. A second omission is allowed to reach the authoritative matcher, which reports `CAPABILITY_PREFLIGHT_UNAVAILABLE` only after the compact full catalog has been considered. Complete discovery is retained for dry runs and deterministic schema validation; filtering changes inference context only and preserves the original tool schemas and metadata.

Schema-aware catalog traversal is bounded to four schema levels, 64 selector values per property, 512 description characters, and 256,000 expanded characters. Selector variants retain a branch description declared beside an exact `oneOf` or `anyOf` binding, allowing a parameterized tool to publish distinct provider-neutral semantics for each finite operation without exposing its implementation. The limit remains a fail-closed safety boundary. Oversize diagnostics include total characters, selected and full server/tool counts, base and variant counts, and the largest contributing tools. No catalog is silently truncated.

Native Flow containers and ordinary deterministic shaping steps are workflow structure, not external capabilities. Sequence, parallel, loop, switch, `set`, workflow-routing, MCP-invocation plumbing, and emit contracts are therefore omitted from capability matching while remaining available to generation and validation. Capability-bearing native steps, including model calls, human interaction, and `decision.evaluate`, remain in the catalog when policy-allowed. This prevents a structural loop from being treated as an alternative or complementary primitive beside the action it repeats.

Each operation is classified as `external_effect`, `human_interaction`, or `local_processing`; external effects are additionally classified as `read`, `write`, `execute`, or owned-resource `lifecycle`. `required` describes whether the generated plan must implement the operation, not whether a conditional effect runs on every execution. The original request, caller context, and every accepted clarification question/answer remain separate provider-neutral evidence sources with stable IDs. `coverage_requirements`, `optionality_evidence`, write-confirmation evidence, and any no-effect outcome reference one source plus one excerpt. A no-effect branch must have exact evidence overlapping that operation's `workflow_structure` requirement; availability, permission, safety-boundary, and ordinary failure policies cannot manufacture an abstention branch for an otherwise required effect. Each coverage requirement also declares `capability_contract` when the selected card itself must document the intrinsic primitive, or `workflow_structure` for cardinality, uniqueness, complete-scope iteration, ordering, conditions, finalization, failure handling, quality thresholds, runtime instructions and argument values, input locator representation, or locally derivable parameter mapping. Capability-card coverage review receives only the first class and judges only its intrinsic primitive even if an evidence excerpt retains structural context; the latter remains locked for workflow generation and validation instead of making a generic parameterized tool appear insufficient. Flow resolves each reference into a stable anchor after Unicode NFC and whitespace normalization while preserving exact case, punctuation, accents, and word order; paraphrases and excerpts spanning sources fail closed. Runtime-dependent operations carry a provider-neutral `decision_source_operation_id` instead of relying on domain words or provider names to guess their source. Operations declare exact backward data-flow edges through `input_operation_ids`; this lets Flow trace a local validation/projection to one upstream capability producer without inferring from descriptions, adjacency, or provider names. Unknown, self, and forward edges are rejected. Constraints are independently classified as `exact_denial` or `workflow_policy`; conditional, ordering, cardinality, coverage, confirmation, and quality rules are structural policies rather than document-wide denials. Matching can select one capability, the smallest complementary composition, a conditional set of selector variants, or no capability for local work.

After matching, an independent provider-neutral coverage review compares every grounded requirement against the exact selected catalog cards. Requirement IDs carry their canonical anchored excerpts through review and rematching; structured verdicts are accepted only when operation IDs, requirement IDs, catalog IDs, and catalog excerpts validate deterministically. Before an incomplete verdict can unlock matching, one bounded evidence-grounded adjudication separates a genuinely absent intrinsic primitive from differences that belong only to workflow structure. The latter is canonicalized with reason `capability_coverage_workflow_structure_canonicalized`; its controlled facets cover cardinality, uniqueness, scope iteration, ordering, conditions, confirmation, finalization, failure/cancellation, quality thresholds, runtime arguments, input representation, and local mapping. Adjudication never consults provider, server, tool, URL, catalog-numbering, operation-description, or domain-name heuristics, and malformed adjudication is a model-contract violation rather than a human question. A confirmed intrinsic gap unlocks only the affected operation for one targeted rematch while unrelated decisions remain fixed. If the catalog still documents only a weaker intrinsic behavior, an active top-level intent-clarification session may present one relaxation form whose recommended option preserves the original requirement and stops. Only explicit acceptance of the documented weaker behavior restarts planning with the accepted question/answer as a concrete clarified-intent evidence source. The same evidence fingerprint is never asked twice; preserved requirements, exhausted clarification budgets, and repeated gaps fail with `CAPABILITY_PREFLIGHT_UNAVAILABLE` and reason `incomplete_effect_coverage`.

A conditional capability set is accepted only with a grounded decision contract. Flow first uses a selected producer capability's typed enum output when it covers every effect branch. If the locked decision source cannot cover those values, Flow inspects only declared `input_operation_ids`, tracing local-processing edges without descriptions or names. One typed enum producer remains preferred, followed by exactly one structured-decision-capable semantic root of a selected artifact composition. When the declared decision operation is `local_processing`, has at least two distinct valid physical upstream operations, and `decision.evaluate` is registered and policy-allowed, Flow may instead synthesize one local evaluator. Selector schemas supply its effect values, and only validated inventory evidence supplies a no-effect value. Stable opaque field names allow one evaluator to own several conditional groups. Every upstream operation must participate in its boolean conditions. Equal or incomparable physical roots without that declared local reducer remain `conditional_decision_source_ambiguous`; absent sources remain `conditional_decision_source_unavailable`. These are technical lineage outcomes and never ask the human to choose tools or predict the runtime value.
The internal matching contract distinguishes mutually exclusive selector alternatives (`conditional_mode=exactly_one`) from a guarded effect or ordered conditional composition (`conditional_mode=all_on_value`). `exactly_one` still requires at least two distinct branches. `all_on_value` accepts one or more structurally distinct MCP invocations only when the operation explicitly declares a no-effect outcome, and assigns catalog order as execution order; every selected capability then runs once inside the single effect case and none runs in every declared no-effect case. This represents both one guarded write and an explicitly matched multi-call effect without inferring provider-specific lifecycle semantics.
When the declared intent also permits an outcome with no external effect, additional enum values are retained as explicit non-mutating switch cases instead of being forced into a write branch. If a single selected `llm.call` or MCP producer has no suitable output enum, Flow can project a provider-neutral strict `structured_output` contract at `/json/decision`; the synthesized enum contains every effect branch plus a collision-free `NO_EFFECT` value when required. Shared structured or local producers receive one stable opaque-operation-derived field per conditional operation. For a local multi-source reducer, each declared upstream operation is routed into a distinct required opaque boolean input on its decision-owning leaf, preserving exact typed lineage across the parent graph without deriving meaning from labels or domains. Local evaluators must contain exactly all locked fields, exact effect cases, an allowed no-effect default only when declared, and no fixed or non-expression conditions; ad-hoc `set`, functions, and caller-input discriminators remain invalid. Pipeline leaf generation validates one locked producer and unchanged typed public decision outputs through direct expressions or transparent projections. Leaf preparation also validates switch shape, literal case coverage, exact call placement/order, explicit no-effect cases, and the non-mutating default before parent assembly. Final assembled-document validation retains complete cross-workflow lineage checks and routes an unproven producer-to-consumer decision path to parent-graph repair. Final validation proves the producer identity, output path, enum, input participation, switch coverage, ordered composition calls, non-mutating no-effect cases, and non-mutating default.

Multiple independently required read variants without this proof remain composed, unconditional calls; ambiguous writes, execution, or lifecycle effects fail closed. For one physical capability, selector bindings define a specificity order. Any whole-tool or partial-selector entry whose bindings are a strict compatible subset of a selected descendant is removed as a representational ancestor. A unique maximal descendant is canonicalized to one match with reason `selector_ancestor_chain_canonicalized`; multiple incomparable maxima remain alternatives. A malformed advisory set is canonicalized to a conditional match only when those maxima prove one mutually exclusive selector family and the locked operation declares a decision source, emitting `conditional_selector_family_canonicalized`. When a declared alternative set collapses to one logical capability, it is normalized to `unavailable` with reason `selector_ancestor_chain_insufficient`; representational ancestors cannot stand in for missing effect branches. Every referenced ID must be known; absent decision sources, incompatible families, and ungrounded branch values remain fail-closed contract outcomes. During the one bounded matching repair, an ungrounded conditional set unlocks its upstream decision operation as a coupled repair unit while preserving unrelated valid matches. If repair still cannot establish a safe contract, the result is the actionable `CAPABILITY_PREFLIGHT_UNAVAILABLE` reason `conditional_decision_contract_gap`, not a request for the user to predict a runtime outcome. Grounding attempts emit sanitized counts and `gnougo-flow.plan.capability_matching.conditional_grounding` events containing only operation/catalog IDs, paths, enum values, contract source, and failure code. A successful source correction additionally emits normalization reason `conditional_decision_source_canonicalized` with only operation IDs, selected catalog IDs, the producer catalog ID, and contract source; ordered conditional composition emits `conditional_composition_canonicalized`. Accepted conditional sets are emitted under one expression-based `switch`: `exactly_one` variants have distinct literal case values, while every `all_on_value` member shares one effect case and is validated in locked order.
A one-capability `exactly_one` selector or a one-capability `all_on_value` match without an explicit no-effect contract remains invalid and is never converted into an unconditional write.
Pipeline composition preserves the extractor's exact operation, opaque catalog, method, and selector claims. Split conditional effect variants are consolidated under their earliest exact variant owner; the decision-producing leaf is never selected unless it actually claims a branch. Missing or ambiguous locked ownership fails extraction repair instead of falling back to lexical leaf, provider, tool, or domain scoring. Required unconditional capability occurrences are a multiset: two operations selecting the same tool still require two statically verifiable calls. Local operations remain semantic blueprint obligations instead of being forced onto an arbitrary native step. Constraint matching also deduplicates repeated exact IDs; a constraint that references only native Flow capabilities is normalized to provider-neutral orchestration policy because exact denied alternatives are an MCP-only contract.

The single-selector outcome depends on catalog evidence: `conditional_selector_set_insufficient` or `selector_ancestor_chain_insufficient` is `unavailable` only when no compatible sibling exists in the catalog. If the catalog contains a compatible sibling that the model omitted, the response remains a repairable model-contract violation.

Matching-contract failures retain the model-reported status, selected and candidate counts, and bounded invalid-field names. Each `matching_issues[]` entry exposes a stable `validation_issue` such as unsupported status, malformed arrays, unknown IDs, missing reason, selection cardinality, decision reference, conditional mode/topology, or local-processing mismatch. The one repair prompt receives these structured diagnostics while valid unrelated matches remain locked; `reason_code=model_repair_exhausted` describes only the exhausted repair outcome, not the underlying defect. Prompts, credentials, response bodies, and model reasoning are never included.

MCP tools may advertise the versioned `_meta.gnougo.artifacts` contract. Flow
uses its domain-neutral artifact kinds and JSON pointers to compose producers
with consumers: one materialized output can feed multiple later leaves, and the
main workflow must route that exact value without constructing a locator.
Before model repair, inferred matching recursively closes required artifact
dependencies to one bounded fixed point. A unique minimal acyclic producer
graph is composed automatically, including multi-hop graphs; multiple minimal
graphs remain ambiguous, while missing, cyclic, or over-limit graphs fail
closed with sanitized `artifact_closure_*` reason codes. Producer reuse prefers
capabilities selected by declared upstream `input_operation_ids`.
Explicit metadata is authoritative; schema/description inference remains only
for external MCP compatibility. Final validation traces required consumer
values across direct calls, transparent `set` aliases, and typed workflow
input/output boundaries; invented, transformed, or kind-incompatible values
are rejected. When preflight is enabled, an artifact
materializer with no remaining locked capability occurrence fails with
`CAPABILITY_PREFLIGHT_REDUNDANT_ARTIFACT_PRODUCER`. Multiple explicitly
requested source operations still produce multiple locked occurrences.

MCP tools may also advertise `_meta.gnougo.composition` version `1`. A
`complete_operation` lists lower-level tools or prompts from the same server
that it encapsulates. Candidate expansion keeps the wrapper visible when a
phase is selected, and matching normalizes wrapper-plus-phase ambiguity to the
complete operation so generated workflows do not start a session they never
finish or invoke both layers redundantly. Invalid or self-referential metadata
fails closed.

Selector variants are also canonicalized structurally before repair. When a
single final match repeats one physical whole-tool card alongside exactly one
of its bound variants, Flow removes only the redundant whole-tool card and
keeps the variant's literal bindings. It never chooses between sibling variants
from operation wording, provider names, tool names, or domain keywords.

When a grounded runtime decision crosses pipeline leaf boundaries, Flow owns
the technical boundary contract: the producer output and every conditional
consumer input are canonicalized to the same required string enum and routed
unchanged through main. Incompatible extractor schemas are replaced by that
locked contract, and bounded extraction retries recompute boundary diagnostics
from the current candidate instead of carrying stale errors forward.

The matcher computes completeness deterministically and performs at most one repair while retaining valid unrelated decisions. Its independent coverage review and gap adjudication receive only the exact selected capability cards. Their strict response schemas lock operation, requirement, and selected catalog IDs and bound every array and excerpt; gap adjudication additionally locks a controlled structural-facet vocabulary. Unicode normalization and whitespace folding are allowed for copied evidence, while case and punctuation remain exact. If either review violates its contract, Flow records bounded field-level issue codes and makes at most one repair call with the rejected bounded candidate and precise issue list. A repeated violation fails closed with sanitized `contract_issues` and a retry-or-change-model recommendation instead of an opaque review error or HITL.

When the top-level intent clarification session is active and only genuine user-intent ambiguity remains, it may spend its remaining shared form budget and then restart complete discovery, inventory, matching, and generation. The legacy `capability_preflight.clarification.enabled` contract remains available when top-level clarification is off. Missing providers, timeouts, incomplete forms, malformed contracts, unavailable reads/execution, lifecycle gaps, mixed blockers, or ambiguity after the applicable retry fail closed. Flow never asks the user to predict a runtime-dependent branch, repair catalog cardinality, choose catalog IDs, or assemble an artifact producer graph. After matching repair is exhausted, a separate behavior-only relaxation is eligible only if every remaining blocker is a conditional external write and a safe required read/execution result remains. The form offers preserve-and-stop first, or explicit read-only continuation; accepting restarts complete preflight once, while deduplication and the shared budget prevent repeated prompts. Confirmed required omissions and unresolved conditional decision-contract gaps use `CAPABILITY_PREFLIGHT_UNAVAILABLE`; malformed or unknown-ID decisions use `CAPABILITY_PREFLIGHT_INFERENCE_FAILED`. Both expose bounded sanitized diagnostics and current submitted form/question counts without prompts, answers, or model reasoning. The inventory declares an `external_write_confirmation_policy` of `required`, `forbidden`, or `unspecified`, backed by exact request/context evidence for either explicit choice. Flow injects a required `human_interaction` operation and ordering constraint before the first external write unless that validated policy is `forbidden`; this replaces language-dependent keyword detection and keeps `unspecified` fail-safe.

Ordinary `workflow.plan` callers remain compatible because the default is `off`. `explicit` mode performs the same deterministic validation without an inference call:

```yaml
capability_preflight:
  mode: explicit
  requirements:
    - id: load_object
      description: Load an object from configured storage.
      required: true
      alternatives:
        - server: object-storage
          kind: tool
          method: get_object
          request_bindings:
            - path: /method
              value: get_metadata
        - server: archive-storage
          kind: prompt
          method: retrieve_object
    - id: send_notification
      description: Notify the caller when processing finishes.
      required: false
      alternatives:
        - server: messaging
          kind: tool
          method: send_message
  constraints:
    - id: preserve_source
      description: Never delete the source object.
      required: true
      denied_alternatives:
        - server: object-storage
          kind: tool
          method: delete_object
          request_bindings:
            - path: /mode
              value: permanent
```

`request_bindings` are optional for backward compatibility. Each binding is an RFC 6901 JSON Pointer relative to `mcp.call.input.request` and a JSON scalar value documented by the discovered input schema. When present, the generated call must contain that exact literal request value; expressions and opaque request construction do not satisfy it. Selector-aware denials reject only the matching logical variant, while a denial without bindings rejects the whole tool or prompt. Discovery, inference, and availability failures return `CAPABILITY_PREFLIGHT_DISCOVERY_FAILED`, `CAPABILITY_PREFLIGHT_INFERENCE_FAILED`, and `CAPABILITY_PREFLIGHT_UNAVAILABLE` respectively.

#### Auto and basic modes

`mode: auto` is the default. It performs one classifier LLM call before generation and returns the classifier result under `meta.mode_selection`. The classifier estimates complexity by counting meaningful branches such as conditions, switch/case paths, loops, retries, error handling, cleanup paths, validation branches, tool-orchestration choices, and state transitions.

Use `mode: basic` to skip classification and run the original single workflow-generation path directly. Use `mode: pipeline` to force decomposition. Use `mode: repair` to make a targeted patch-style repair to an existing workflow while still returning a complete replacement YAML document.

#### Repair mode

Use `mode: repair` when a workflow already exists and should be minimally changed because of a runtime error or an explicit repair instruction. The LLM receives the existing YAML, optional failed input, optional runtime error details, and optional user repair instructions. It must preserve public inputs, outputs, workflow identity, behavior, and MCP choices unless the repair evidence proves they are wrong.

```yaml
- id: repair_plan
  type: workflow.plan
  input:
    mode: repair
    generator:
      model: gpt-4o
      reasoning: medium
      prefilter: true
      context: "Keep this compatible with persisted chat-agent workflows."
    repair:
      existing_yaml: "${data.inputs.current_workflow}"
      prompt: "${data.inputs.user_repair_instruction}"   # Optional when error.message is present
      failed_input: "${data.inputs.failed_user_prompt}"   # Optional
      error:                                             # Optional when prompt is present
        code: "${data.inputs.error_code}"
        type: "${data.inputs.error_type}"
        message: "${data.inputs.error_message}"
        details: "${data.inputs.error_details}"
      scope:                                             # Optional surgical lock
        workflow: "${data.inputs.failed_workflow}"
        step_id: "${data.inputs.failed_step_id}"
    validate:
      mode: strict
      dry_run: true
      max_repair_attempts: 3
```

`repair.existing_yaml` is required. At least one of `repair.prompt` or `repair.error.message` must be present. When `repair.error` is present, `repair.error.message` is required. When `repair.scope.step_id` is set, validation becomes surgical: every workflow, local `workflow.call`, step ID/type/order, branch, skill, and public contract must remain unchanged. Only the identified failing step, its existing direct consumers, and directly dependent output expressions may change. An over-broad proposal is rejected with `REPAIR_SCOPE_VIOLATION` and can be reprompted within the configured attempt bound. The returned shape is the same as other modes: `{ workflow, yaml, meta, diagnostics }`, with `meta.mode: repair`.

#### Pipeline mode

Use `mode: pipeline` when the input is a raw user automation prompt that should be cleaned up, segmented into leaf subworkflows, and assembled into one local YAML document.

```yaml
- id: plan_pipeline
  type: workflow.plan
  input:
    mode: pipeline
    name: source-record-report
    skill:
      description: Build a report from records in a configured source.
      tags: [records, report]
      inputs:
        target_collection:
          type: string
          required: false
          default: inventory-main
        number_of_records_to_process:
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
2. `mark_extractable_blocks` uses strict structured extraction when the resolved model explicitly supports structured output. A schema-valid parsed JSON response from an earlier planner phase also proves support for the same resolved provider/model; selecting `capability_preflight.mode: infer` by itself is not proof. Other modes fall back to annotated Markdown when support remains unknown or is explicitly unavailable. Structured extraction returns metadata plus annotated Markdown: the Markdown still wraps only significant algorithmic sections in `:::subworkflow name="..."` blocks and adds a `## Main workflow orchestration` section, while the sidecar records each leaf's description, typed input/output schemas, exact `owned_operation_ids`, and planned MCP tools/prompts to call. Every non-main locked operation ID has at most one declared leaf owner. When that unique structural claim exists but the extractor omitted a matching planned-tool entry, deterministic composition restores every exact locked catalog occurrence on that owner; unknown or duplicate ownership fails closed and is repaired without using descriptions, provider/tool names, catalog numbering, or leaf order.
3. `extract_subworkflow_specs` parses those blocks as-is, builds generation prompts, and reports validation errors for nested blocks or subworkflow-call mentions.
4. `generate_subworkflows` runs the normal `workflow.plan` generator for each leaf workflow in parallel. Each leaf prompt contains only that leaf's goal, input/output contract, and content; leaf generation forbids `workflow.call` and `workflow.plan`, preserves the configured MCP prefilter behavior, forces validation, retries failed leaf generation up to the parent repair attempt budget, and rejects bare `type: object` schemas unless they define non-empty `properties`.
5. `assemble_main_workflow` sends a compact leaf manifest, the generated leaf contracts, and a minimal main-graph DSL context to the LLM. The LLM returns only a `document` plus orchestration `graph`; the runtime renders the real `main` workflow deterministically and grafts the validated leaf workflows before final validation. If that graph inserts a pure exact `set` alias between one uniquely proven artifact producer and a compatible consumer input, deterministic normalization replaces the alias with the producer output. Literal, transformed, or multiply sourced values remain fail-closed.

Structured extraction generates one complete initial candidate. Later deterministic or
extraction-quality repairs are bounded patches against the best structurally valid candidate:
`add_leaf`, `replace_leaf`,
`remove_leaf`, `merge_leaves`, or `replace_main_orchestration`. Every patch carries the
exact SHA-256 candidate fingerprint and the exact stable `addressed_diagnostic_ids` it claims
to repair. Diagnostic identities hash the code, leaf, remediation surface, and sorted extraction
evidence paths, so two findings with the same code on different leaves remain independent.
Replacements retain immutable capability ownership and
cannot remove or weaken previously validated input/output schema members;
merges atomically union it; required ownership prevents removal; and new leaves cannot
invent external capabilities. Deterministic validation is rerun after every patch. Valid
candidates are ranked by evidence-qualified critical findings, warnings, semantic score,
and patch size, with the earlier candidate retained on ties. Quality non-improvement,
deterministic regression, and validation non-improvement are counted and reported separately.
Two repeated diagnostic fingerprints or rejected patches stop with `WORKFLOW_PLAN_REPAIR_STALLED` without
emitting a rejected candidate. Legacy non-structured extraction retains complete bounded
regeneration for compatibility.

Extraction schema pruning recognizes both Flow `required_properties` and array-valued JSON
Schema `required`; the boolean Flow input-level `required` flag remains a separate annotation.
A weak required property is preserved and receives an exact nested diagnostic instead of being
deleted. Malformed, blank, duplicate, or undeclared required-property entries fail deterministic
validation with patchable `INVALID_EXTRACTION_INPUT_SCHEMA` or
`INVALID_EXTRACTION_OUTPUT_SCHEMA` diagnostics before quality review.

Every accepted conditional branch group has one shared typed result contract. Leaf blueprints add a path-total projection step after action calls, and public outputs bind to that projection rather than to a branch-local or last action call. A branch-dependent binding or a conditional group without exact enum coverage is rejected before leaf YAML generation.

The extraction quality reviewer must attach request substrings or RFC 6901 pointers into
the canonical extraction or locked capability contract to critical diagnostics. Only a
verifiable critical finding can block. Unsupported claims are downgraded to advisory,
and a score or retry verdict alone cannot reject a deterministically valid candidate. A
malformed structured review receives one retry against the unchanged candidate and then
fails closed as a contract defect. Human clarification is considered only when every
remaining evidence-qualified blocker is `intent_ambiguity`; plan, capability, contract,
provider, and malformed-output defects are never delegated to the user.

Critical extraction diagnostics are retained in a remediation ledger keyed by stable diagnostic identity. After every patch, deterministic extraction validation runs again. An addressed baseline finding disappears when its relevant surface changed, deterministic validation passed, and the delta reviewer no longer reports that identity; positive obligation evidence is allowed to remain. A patch to unrelated orchestration cannot erase a leaf-contract blocker. Delta-review prompts include only changed leaf/main surfaces, hashes of unchanged leaves, referenced capability cards, and baseline blockers rather than the complete annotated extraction and catalog.

Pipeline convergence telemetry is content-bounded: it records candidate hashes, changed leaf
names, addressed IDs, raw and stabilized diagnostic IDs, counts, and stabilization reason codes.
It never records prompts, model responses, secrets, or repository content through these events.

Generated public outputs must remain concrete. Before final validation, Flow strengthens outputs from locked producer contracts where possible and removes only unverifiable or nullable nested properties (including their `required_properties` entries) when the Flow contract cannot represent their exact value set. It never narrows nullable values to non-null scalars and never invents array item or root-output types; a weak root contract still fails with `WEAK_OUTPUT_SCHEMA` diagnostics.

The final YAML has exactly one hierarchy level: `main` may call local leaf workflows with `workflow.call`, while leaf workflows must never contain `workflow.call` or `workflow.plan`. The returned `pipeline` object includes `normalized_markdown`, `annotated_markdown`, and parsed `specs`; each spec includes `description`, `input_schemas`, `output_schemas`, and `planned_tools`.

When structured extraction is active and `planned_tools[].required` is true, leaf generation must emit an explicit direct `mcp.call` with matching `input.server`, `input.kind`, and literal `input.method` or `input.methods`. Pipeline validation rejects a generated leaf that omits a required planned tool. If pipeline-level MCP context was built, extraction also verifies planned server/tool/prompt names against the discovered capabilities; otherwise final MCP-aware validation still checks generated calls against the runtime registry.

Locked capability occurrences are assigned as an exact multiset. Deterministic ownership normalization excludes local shaping leaves, rewards positive action-family agreement between a capability and a leaf, and ignores actions mentioned only as prohibitions. This keeps complementary capabilities in cohesive producer/action leaves without relying on product or server names.

When a generated leaf workflow contains root-level helper functions, final assembly moves those helpers into the grafted leaf workflow's own `functions:` block. They are not promoted to the final document root, so helpers remain isolated with the leaf that uses them.

Standalone generated leaf:

```yaml
version: 1
name: parse-resource
functions: |
  function parseResourceId(resourceId) {
    var parts = resourceId.replace(/\/$/, "").split("/");
    return {
      namespace: parts[parts.length - 2],
      item: parts[parts.length - 1]
    };
  }
workflows:
  parse_resource:
    inputs:
      resource_id: { type: string, required: true }
    steps:
      - id: parsed
        type: set
        input:
          value: "${functions.parseResourceId(data.inputs.resource_id)}"
    outputs:
      namespace: "${data.steps.parsed.value.namespace}"
      item: "${data.steps.parsed.value.item}"
```

Composed pipeline document:

```yaml
version: 1
name: resource-pipeline
workflows:
  main:
    inputs:
      resource_id: { type: string, required: true }
    steps:
      - id: parse
        type: workflow.call
        input:
          ref: { kind: local, name: parse_resource }
          args:
            resource_id: "${data.inputs.resource_id}"
    outputs:
      namespace: "${data.steps.parse.outputs.namespace}"
      item: "${data.steps.parse.outputs.item}"

  parse_resource:
    functions: |
      function parseResourceId(resourceId) {
        var parts = resourceId.replace(/\/$/, "").split("/");
        return {
          namespace: parts[parts.length - 2],
          item: parts[parts.length - 1]
        };
      }
    inputs:
      resource_id: { type: string, required: true }
    steps:
      - id: parsed
        type: set
        input:
          value: "${functions.parseResourceId(data.inputs.resource_id)}"
    outputs:
      namespace: "${data.steps.parsed.value.namespace}"
      item: "${data.steps.parsed.value.item}"
```

Configured `name`, `skill`, and public input schemas are authoritative and are preserved exactly in the root skill and `main` workflow. Leaf inputs are call arguments and are not automatically promoted to public inputs; the main assembler maps public names to leaf argument names and derives internal values in workflow steps. When no structured contract is configured, the final assembly phase infers the public contract from the normalized user request, but leaf call arguments and available outputs come from the actual generated leaf workflows rather than the initial extraction draft. Composition rejects any `data.inputs.<name>` reference that is not declared by the resolved main input contract, and it also rejects calls that omit required arguments from the generated leaf contract.

`validate.max_repair_attempts` controls the bounded number of repairs after the initial candidate. When it is absent, legacy `on_invalid.max_attempts` continues to mean the total candidate-attempt limit for compatibility, then the default total is 3. The repair budget is applied to extractable-block annotation, each leaf generation, and the final main-workflow assembly. If block extraction validation fails, the next `mark_extractable_blocks` attempt receives the previous annotated Markdown plus exact validation feedback. If final parsing, policy, hierarchy, compilation, or semantic validation fails, the next assembly attempt receives the previous YAML response and structured validation error so it can repair the complete `document` and `graph` mapping.

Planner response formatting is strict-first and internal. When structured output is supported or already proven for the selected target, normalization returns `normalized_markdown`, leaf/basic generation returns a versioned envelope containing `yaml`, and parent assembly returns separate `document_yaml` and `graph_yaml` fields. Repair envelopes lock the immutable contract, base candidate, diagnostic fingerprint, and addressable diagnostic codes with schema enums. A missing or schema-invalid JSON response is retried once with the identical contract and then fails as `LLM_SCHEMA`; it is never silently downgraded to text. Models whose support is unavailable or still unknown retain the legacy text path. These envelopes do not change the public `workflow.plan` output or generated YAML syntax, and every YAML payload still passes parsing, policy, compilation, semantic, capability-lineage, artifact-provenance, and optional dry-run validation.

Each generation phase keeps one best candidate. A repair may replace it only by reaching a later validation checkpoint or strictly reducing the blocking diagnostic-identity set; an identity combines the validated code with its workflow, step, field, or path, so removing one of several same-code blockers is measurable progress. Stale fingerprints, unchanged candidates, and regressions are rejected atomically; two non-improving responses terminate with `WORKFLOW_PLAN_REPAIR_STALLED`. A new contract epoch is opened only when the immutable leaf/main contract fingerprint changes. Telemetry records only response mode, schema version, phase/attempt, fingerprints, epoch, progress, diagnostic counts, and stall reason—not prompts, responses, YAML, credentials, or human answers.

The final composed pipeline document uses the same validation sequence as standard `workflow.plan`: policy and limits are enforced, compiler validation and MCP-contract-aware semantic validation are always forced, and `validate.dry_run` executes the complete entrypoint with deterministic fake LLM, MCP, and human-input providers. `validate.compile: false` is accepted only as a legacy no-op and cannot disable strict validation. MCP discovery contracts are collected once for final validation, and every assembly attempt emits its own `workflow.plan.validate` telemetry span.

**Output:** `{ workflow: { version, name, workflows: [...] }, yaml: "...", meta: { model, attempt?, mode, mode_selection? }, diagnostics: [...], pipeline? }`

**Features:**

- **Automatic MCP discovery**: Connects to all configured MCP servers, lists their tools/prompts, and injects them into the planning prompt so the LLM knows what's available. A transient discovery failure is retried up to three total attempts with progressive 500 ms and 1,000 ms delays.
- **MCP pre-filter**: Uses a lightweight LLM call to select only the MCP servers/tools relevant to the task instruction — reduces prompt size and cost.
- **Full DSL reference injection**: The LLM receives the complete DSL documentation (step types, expressions, error handling) so it can generate valid workflows.
- **Policy enforcement**: Generated workflows are validated against allowed/denied step types and max step limits.
- **Mandatory strict validation before acceptance**: `workflow.plan` always runs the validator, compiler, and semantic checks before returning a plan. `validate.compile: false` is tolerated for older workflows but is ignored. This catches non-fatal validator diagnostics such as unknown step types, invalid container shapes, unknown YAML structural keys, future step references, conditional branch/loop mapping errors, and invalid `data.steps.<id>.response.<field>` mappings.
- **Structured repair diagnostics**: Validation and `dry_run` failures include machine-readable `details.diagnostics[]` entries with stable codes, locations, hints, expected shapes, allowed paths when available, the deterministic sample inputs, selected branch/default container path, source expression when known, and `llm_guidance` for reprompt repair.
- **Executor-owned step contracts**: Every registered executor must expose declarative JSON Schema input/output contracts. Planning validation recursively rejects missing required fields, wrong literal types, unknown keys (including nested keys), and mutually exclusive fields. A custom registered executor without a contract fails closed.
- **Static expression type inference**: Exact `${...}` references inherit types from workflow inputs, previous step outputs, `set.output_schema`, and scoped loop variables; embedded interpolation is a string, and built-ins such as `len`, `toNumber`, `exists`, `json`, `pick`, and `omit` have known result types. Incompatible assignments such as `llm.call.text` into `max_tokens` or `data.<item_var>.title` into an integer workflow-call input fail with `EXPR_TYPE_MISMATCH` before dry-run. Opaque/custom expressions remain runtime-validated.
- **Local workflow-call contracts**: Literal local `workflow.call` targets are validated against the called workflow's declared inputs. Missing, extra, and wrongly typed `input.args` fail during plan validation, and the called workflow's typed outputs are propagated so invented paths like `data.steps.call.outputs.unknown` are rejected.
- **Optional dry-run validation**: Set `validate.dry_run: true` to execute the generated workflow once with deterministic fake LLM, MCP, human-input, and routing providers. A valid declared input default is preferred; otherwise a string enum uses its first declared value, while unconstrained strings retain the synthetic sample. This catches runtime input-resolution errors such as free-form `llm.call.text` being used where a number is required. Dry-run MCP sessions expose only discovered tools: an invented method fails instead of receiving a generic mock response. The dry-run never calls real LLMs or MCP tools.
- **MCP output contracts**: MCP discovery injects complete `input_schema`, `output_schema`, and `example_response` metadata into the planning prompt. `output_schema` / `example_response` define which fields may be read from `mcp.call` single-tool `response` objects.
- **Fail-closed MCP discovery**: A generated tool-mode `mcp.call` is rejected when its server catalog is missing, discovery failed after all retries, or the discovered catalog is empty. Dynamic server names cannot pass plan validation because their existence cannot be proven.
- **MCP envelope, target, and request validation**: Unknown fields at `mcp.call.input` are rejected with a suggestion to move tool arguments under `input.request`. Literal `method` and every literal entry in `methods` must exist in the discovered server contract. The shared request is validated against every selected tool schema.
- **Expanded JSON Schema checks**: MCP requests support `enum`, `const`, `allOf`, exact `oneOf`, `anyOf`, string length/pattern, numeric bounds/multiples, object/array size, `uniqueItems`, nested schemas, and schema-correct `additionalProperties` behavior. Quoted numeric, integer, and boolean YAML scalars remain strings during `workflow.plan` validation and fail when the contract requires real booleans or numbers.
- **Runtime request validation**: Requests containing expressions or `request_template` are checked again after resolution/rendering and immediately before `CallToolAsync`. The live tool catalog proves the method exists, and the resolved request must satisfy that tool's `input_schema`.
- **Bounded automatic self-correction**: If the generated YAML is invalid (parse error, policy violation, compilation error, semantic mapping error, or optional dry-run failure), the structured error is sent back to the LLM for up to `validate.max_repair_attempts` repairs after the initial candidate. Legacy `on_invalid.max_attempts` remains a total-attempt limit, and the default remains three total attempts. `on_invalid.action` is accepted only for compatibility and cannot disable automatic repair while attempts remain.
- **Repair-stall detection**: Validation diagnostics and YAML structure are normalized and fingerprinted. Two non-improving repair responses or a repeated diagnostic/structure cycle stop leaf repair with `WORKFLOW_PLAN_REPAIR_STALLED` and return control to extraction/blueprint repair instead of consuming the remaining repair budget. Repair prompts retain only the best current YAML and a bounded, deduplicated diagnostic history.
- **OpenTelemetry tracing**: Full GenAI convention traces for the planning LLM call, MCP discovery, and pre-filter phases.

Workflow execution traces also include injected workflow inputs on the workflow span:

- `gnougo-flow.workflow.inputs` as a single JSON string with secret-looking keys such as `token`, `password`, `secret`, and `api_key` redacted.
- `gnougo-flow.workflow.inputs.count`
- `gnougo-flow.workflow.inputs.keys`

**Semantic mapping guardrails:** generated plans must not read `data.steps.<id>.*` from steps that are produced only inside a `switch` case, an `if`-guarded step, or a loop body unless that value is first mapped into a guaranteed location. Function arguments are evaluated eagerly, so `coalesce(data.steps.fix.value, data.steps.question.value)` is still unsafe when either step may not have executed. Prefer a common workflow-level output alias in every branch, or a guaranteed normalization step with a stable output schema.

Loop outputs need special care: `data.steps.<loop_id>.results` is an array of per-iteration `data.steps` snapshots, not an array of the last child step's output. If a loop child `set` step named `build_item_result` produces `processed`, post-loop code must read `iteration.build_item_result.processed`. To produce a flat list, create a typed child `set` step in the loop and flatten/filter through that child step id.

Generated YAML should preserve typed scalars: emit `required: false`, `strict: true`, `timeout_ms: 1200000`, and `append: false` as booleans/numbers, not quoted strings. Use literal block scalars (`|`) for multiline prompts/templates or strings containing JSON/double quotes. Required string fields must be present and non-empty; use an optional nullable string field when empty text is a valid value.

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
| `MaxExpressionStatements` | `1000000` | Jint statement budget for bounded generated data transformations. |
| `ExpressionTimeoutSeconds` | `15` | Evaluation timeout. |
| `ExpressionMemoryLimitBytes` | `50000000` | Jint memory limit. |

Increase these limits only for trusted workflows; prefer simplifying expressions or moving complex logic to WFScript functions.

---

## WFScript — Custom JavaScript Functions

Define reusable functions in the `functions:` block (document-level or workflow-level).
When `workflow.plan` generates custom functions, each generated `function` must be immediately preceded by JSDoc with typed `@param` entries for every parameter and a typed `@returns` entry for the output:

Before generated YAML is validated, the planner may add a missing exact-name `@param` tag only when deterministic JavaScript usage proves a coarse semantic type such as object, array, string, number, or boolean. Ambiguous parameters are not guessed and continue to fail validation with `FUNCTION_JSDOC_PARAM_MISSING`.

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
| `DECISION_EVALUATION_UNRESOLVED` | No | A finite decision has overlapping matches or no match/default |
| `LLM_TIMEOUT` | Yes | LLM request timed out |
| `LLM_NETWORK` | Yes | Typed transport/rate-limit/service failure, including bounded HTTP `425`, `429`, `500`, `502`, or `503` recovery exhaustion |
| `LLM_PROVIDER` | No | Provider rejected the request with another `4xx` response |
| `LLM_BUDGET_EXCEEDED` | No | An LLM call, token, elapsed-time, or estimated-cost budget was exceeded |
| `LLM_BUDGET_UNVERIFIABLE` | No | Usage, pricing, or a configured durable ledger could not verify an active budget safely |
| `MCP_CONNECTION_ERROR` | Yes | Cannot connect to MCP server |
| `MCP_TOOL_ERROR` | No | MCP tool returned an error |
| `CAPABILITY_PREFLIGHT_UNAVAILABLE` | No | A required operation has no exact available capability |
| `CAPABILITY_PREFLIGHT_DISCOVERY_FAILED` | No | A required catalog could not be discovered reliably |
| `CAPABILITY_PREFLIGHT_INFERENCE_FAILED` | No | Capability inventory inference was invalid or incomplete |
| `CAPABILITY_PREFLIGHT_REDUNDANT_ARTIFACT_PRODUCER` | No | The workflow contains an artifact materializer that was not locked by capability preflight |
| `WORKFLOW_PLAN_REPAIR_STALLED` | No | The same diagnostics survived two repair attempts |
| `TEMPLATE_PLAN` | No | `workflow.plan` failed to generate valid YAML |
| `TEMPLATE_POLICY` | No | Generated workflow violates policy constraints |
| `HUMAN_INPUT_TIMEOUT` | No | User didn't respond within `timeout_ms` |
| `NO_HITL_PROVIDER` | No | No human input provider configured |

Injected `ILLMClient` implementations can throw the provider-neutral, redacted
`LLMClientException`. Its failure kind, retryability, optional status code, safe provider
code, actual attempt count, retry-exhaustion flag, and accepted `Retry-After` are mapped to
the stable errors above. Legacy clients remain supported through HTTP-status classification
only; message text never determines retryability. Workflow error metadata includes only
sanitized stage, classification, retryability, status, attempts, exhaustion, retry timing,
safe provider code, and recommended action. It does not carry endpoints, response bodies,
prompts, credentials, client identities, or scopes.

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
