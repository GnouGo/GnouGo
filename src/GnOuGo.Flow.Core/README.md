# GnOuGo.Flow — YAML Workflow DSL Engine

`GeneratedFunctionDocumentation.Validate` exposes the generated-workflow JSDoc
requirements for earlier construction checks. It uses the same validation as final
semantic review and reports missing typed parameters and return documentation.
Documentation does not establish executable output provenance or runtime success.

Continuing `on_error` handlers on `mcp.call` and `llm.call` with
`structured_output` must return a `json` member satisfying that schema. The
engine validates resolved fallback values before publishing a successful step
result. Invalid fallbacks stop downstream execution with
`STRUCTURED_FALLBACK_INVALID`; workflow finalization still runs.

<a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/v/GnOuGo.Flow.Core.svg" alt="NuGet version"></a>
<a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt=".NET 10.0"></a>
<a href="https://nugettrends.com/packages?ids=GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/dt/GnOuGo.Flow.Core.svg" alt="NuGet downloads"></a>

Declarative workflow engine based on a YAML DSL, **NativeAOT**-compatible (.NET 10).
Write YAML workflows that orchestrate LLMs, MCP servers, templates, loops, human input, and dynamic code generation — all from a single file.

## Typed planning

`workflow.plan` invokes the injected `IWorkflowPlanner` and `IPlanningRuntimeFactory`.
Hosts reference `GnOuGo.Flow.Planning` for the sole Planner v2 implementation. Core
retains provider-neutral contracts and runtime validation without referencing another
GnOuGo project. See [workflow planning](../../docs/workflow-planning-v2.md).

Schema-5 contracts include owned references, bounded decision pages, typed outcomes
and durable correction lineage. `FinalReview` waits for exact-hash approval before
returning `ValidWorkflow`. Missing business choices return `NeedUserClarification`;
technical stops remain distinct from proven `Unsupported`. Injected model capability
resolvers must declare supported reasoning levels for the phase profile.
Business decision records retain governing references, applicability, exclusions and
typed selections. Clarifications expose canonical choices, labels and justified
preference reasons; their resolution belongs to the injected planner. The general
`human.input` DSL and Schema-5 persistence format remain unchanged.

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
  - [workflow.plan](#workflowplan--typed-workflow-planning)
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

Planner v2 can pass `PlanningArtifactBinding` values through `IPlanningRuntime.ValidateAsync`
to preserve compiler-derived operation ownership during artifact validation. The Core runtime
checks these bindings against locked capabilities and actual executable calls, including
the exact confirmation producer. Existing callers without bindings retain legacy validation.

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

var inputs = new JsonObject
{
    ["topic"] = "GnOuGo.Flow"
};

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

Documented action selectors (`method`, `action`, `operation`, `command`, `mode`, `event`, `kind`, JSON Schema `const`, and explicit discriminators) must resolve to documented scalars. Validation accepts literals, proven finite expressions, and direct required enum references from runtime-checked `set` outputs. Optional, nullable, opaque, conditional, or unchecked fallback values cannot prove a selector. Generated expressions cannot hide or replace the logical MCP operation selected during planning.

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

Generated `set.output_schema` values use JSON Schema. Deterministic lowering converts typed contracts to the required workflow or JSON Schema representation. Concrete nullable unions remain intact because they are enforceable by the JSON Schema runtime.

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

`data._loop_previous_<step-id>` exposes the previous completed iteration's step
results to the `while` condition and loop body. It is null before the first iteration
and restored or removed when the loop exits, including failure and cancellation.
Each nested sequential loop owns its snapshot. Planner v2 selects these results
through typed nullable bindings; it does not infer continuation from unrelated steps.

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

### `workflow.plan` — Typed workflow planning

This step invokes the host's injected Planner v2. Missing planner injection fails
explicitly. Clarification, locked capabilities, engine-built behavior review and
deterministic skeletons with bounded typed assignments precede lowering. Compilation,
semantic and scenario validation and final approval are mandatory. Models select issued
references or fill unresolved semantic fields;
`PlanningGraphCompiler` alone produces the reviewed YAML.

```yaml
- id: plan
  type: workflow.plan
  input:
    raw_prompt: "${data.inputs.intent}"
    generator:
      model: "${data.inputs.model}"
      reasoning_profile:
        routine: low
        behavior: medium
        semantic_review: medium
      max_input_tokens: 12000
      max_output_tokens: 8192
    max_concurrency: 4
    max_repairs_per_workflow_gate: 5
    llm_budget:
      max_calls: 100
      max_total_tokens: 15000000
      max_elapsed_ms: 18000000
      unverifiable: fail
```

Inputs cover intent, model configuration, clarification, capability requirements and
constraints, policies, structural limits and budgets. The result contains deterministic
YAML after exact artifact approval. Use `workflow.execute` to execute that artifact.
See [the planner architecture](../../docs/workflow-planning-v2.md) for session contracts,
repair invariants, capability evidence, persistence and deployment.

### `workflow.execute` — Execute a Planned Workflow

Executes a workflow that was dynamically generated by `workflow.plan`.

```yaml
- id: plan
  type: workflow.plan
  input:
    raw_prompt: "${data.inputs.task}"
    generator:
      model: gpt-4o

- id: execute
  type: workflow.execute
  input:
    from_step: plan              # References the workflow.plan step that produced the YAML
```

The plan + execute pattern is the foundation of **agentic workflows**: the user describes a goal in natural language, the LLM plans the steps, and the engine executes them.

---

## Typed Inputs

Runtime entrypoints and workflow calls apply declared defaults to missing input keys
before execution. Explicit null values are preserved and checked separately from
optional presence: a non-nullable optional input may be omitted, but cannot be null.
Nested object members and array items also enforce their declared nullability.

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
Runtime functions use JSDoc with typed `@param` entries for every parameter and a
typed `@returns` entry. Planner models supply only unresolved expression text over
declared parameters. The deterministic compiler generates any required function
wrapper and documentation; repairs cannot replace a global function block.

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
| `TEMPLATE_PLAN` | No | Typed planning stopped before approval |
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

The native `collect_json_arrays(completedLoop.results, ["child", "response", "field"])` expression concatenates original JSON-array strings without altering records or numeric precision. Artifact provenance requires an exact original producer declaring `encoding: "json_array"`; missing, conditional, malformed or transformed source results cannot establish identity. The primitive cannot be overridden by workflow helpers.
