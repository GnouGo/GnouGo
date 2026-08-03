# GnOuGo.Agent (Blazor + Minimal API)

This solution contains:
- **GnOuGo.Agent.Server**: Blazor (server interactive) UI + Minimal API streaming endpoint; published as a trimmed self-contained single-file executable with bundled MCP tools.
- **GnOuGo.Agent.Shared**: shared DTOs

## Architecture

This component is independently testable per `AGENTS.md` rules. It references `GnOuGo.Agent.Mcp`, `GnOuGo.KeyVault.Mcp`, `GnOuGo.DocIngestor.Mcp`, and `GnOuGo.OtlpCollector.Server` as project dependencies, mounting their services in-process to minimise coupling while exposing everything through a single host.

### Runtime topology

```mermaid
flowchart TD
	Desktop[Photino.NET desktop shell] --> Main[GnOuGo.Agent.Server\nmain HTTP host\nhttp://127.0.0.1:<app-port>]

	Main --> UI[Blazor UI\n/]
	Main --> Api[Minimal APIs\n/api/chat\n/api/chat/stream\n/health]
	Main --> AgentProxy[Mounted MCP public route\n/mcp/agent]
	Main --> KeyVaultProxy[Mounted MCP public route\n/mcp/keyvault]
	Main --> DocsIngestorProxy[Mounted MCP public route\n/mcp/docs-ingestor]

	AgentProxy -. current internal proxy .-> AgentInternal[GnOuGo.Agent.Mcp internal loopback host\nhttp://127.0.0.1:<ephemeral-port>/mcp]
	KeyVaultProxy -. current internal proxy .-> KeyVaultInternal[GnOuGo.KeyVault.Mcp internal loopback host\nhttp://127.0.0.1:<ephemeral-port>/mcp]
	DocsIngestorProxy -. current internal proxy .-> DocsIngestorInternal[GnOuGo.DocIngestor.Mcp internal loopback host\nhttp://127.0.0.1:<ephemeral-port>/mcp]

	Main --> OtlpGrpc[Embedded OTLP gRPC\nhttp://127.0.0.1:4317]
	Main --> OtlpHttp[Embedded OTLP HTTP + tenant/debug APIs\nhttp://127.0.0.1:4318]
```

#### Public vs internal ports

- The **public application port** is the main `GnOuGo.Agent.Server` listener, for example `http://127.0.0.1:58443`.
- The mounted MCP routes that the UI and runtime services should use are:
  - `http://127.0.0.1:58443/mcp/agent`
  - `http://127.0.0.1:58443/mcp/keyvault`
  - `http://127.0.0.1:58443/mcp/docs-ingestor`
- Ports such as `60183`, `60683`, `61914`, or `61915` are **ephemeral internal loopback ports** currently used by the mounted MCP implementation.
- Those ephemeral ports are **used** today, but only as a private implementation detail behind the main server proxy. They are not intended as user-facing endpoints.

> Design note: the current implementation already exposes all HTTP MCP traffic through the main server URL. The extra loopback ports exist only because the mounted MCP routes proxy to internal sub-hosts.

## Mounted MCP endpoints

`GnOuGo.Agent.Server` hosts the local MCP HTTP services in-process and mounts them on dedicated routes:

- `GnOuGo.Agent.Mcp` → `/mcp/agent`
- `GnOuGo.KeyVault.Mcp` → `/mcp/keyvault`
- `GnOuGo.DocIngestor.Mcp` → `/mcp/docs-ingestor`

All three mounted services use the stable C# MCP SDK `2.0.0`, explicitly stateless Streamable HTTP, and MCP `2026-07-28` discovery. The public proxy forwards the standardized `MCP-Protocol-Version`, `Mcp-Method`, `Mcp-Name`, and `Mcp-Param-*` headers. Mounted URLs are published to runtime configuration only after every private sub-host is listening; older MCP clients remain supported through SDK negotiation.

The default placeholders in `appsettings.json` intentionally use port `0`:
    
```json
{
  "LLM": {
	"McpServers": {
	  "GnOuGo.Agent.Mcp": {
		"Type": "http",
		"Url": "http://127.0.0.1:0/mcp/agent"
	  },
	  "GnOuGo.KeyVault.Mcp": {
		"Type": "http",
		"Url": "http://127.0.0.1:0/mcp/keyvault"
	  },
	  "GnOuGo.DocIngestor.Mcp": {
		"Type": "http",
		"Url": "http://127.0.0.1:0/mcp/docs-ingestor"
	  }
	}
  }
}
```

At startup, the server replaces port `0` with the actual bound local address and republishes those URLs through the runtime MCP configuration store.

In other words, runtime consumers should treat the mounted MCP endpoints as part of the **same public server** as the Blazor UI and Minimal APIs, even though the current implementation uses private loopback helper listeners internally.

`GnOuGo.Agent.Server` also uses the mounted `GnOuGo.Agent.Mcp` endpoint as the persistence API for local user defaults:

- `user_config_get` — hydrate persisted `default_llm_provider`, `default_llm_model`, and `default_agent`
- `user_config_set` — save updated defaults after `/llm default`, `/llm add` auto-promotion, or `/gnougo select`

The persisted values live in the Agent MCP SQLite database (`Agent:DatabasePath`) rather than only in browser state.
LLM provider and MCP server definitions are hydrated from encrypted KeyVault secrets at startup; `user-settings.json` is no longer used.
`/llm add` can configure `openai`, `ollama`, `copilot`, and `anthropic` providers. The Anthropic provider uses the Anthropic Messages API endpoint `https://api.anthropic.com/v1` and stores the API key encrypted in KeyVault like the other remote providers. The legacy `claude` name is still accepted as an alias for existing configurations.

After model selection, `/llm add` and `/llm edit <provider>` always display an editable review form containing token limits, pricing, structured-output/tool/JSON support, temperature and reasoning behavior, vision/audio/embedding support, supported reasoning efforts, and unsupported request parameters. Exact catalog values remain catalog-backed when accepted unchanged. Edited values and accepted fuzzy/heuristic defaults are persisted as non-secret, provider-qualified overrides such as `openai/model-id`; legacy unqualified overrides remain readable. Approximate matches are restricted to the normalized provider and the UI identifies the source model and similarity before saving.

Bundled MCP servers can also expose selected editable fields through the `BundledMcp` settings section. Each field maps to a KeyVault secret and a runtime target such as `env:Git__Token`, so `/mcp list` can show the bundled server and `/mcp edit <name>` only prompts for the configured fields. The default configuration makes `GnOuGo.Git.Mcp` listable and exposes only the Git token; the token is saved encrypted in KeyVault and injected into the Git MCP process as `Git:Token` when runtime MCP options are hydrated.

## Dynamic workflow input composer

The Blazor chat composer resolves the active/default agent workflow through `SmartFlowService` and adapts the user input area to the workflow `inputs` declaration:

- prompt-like workflows keep the compact single textarea when there is one required `task`/`prompt`/`query`/`request`/`input`/`message` string input, with optional defaulted inputs hidden;
- workflows with multiple required inputs render one field per top-level input;
- object inputs with declared `properties` render nested fields;
- `array`, `object`, `dictionary`, and `any` fields accept JSON or YAML text;
- the UI sends structured `JsonObject` workflow inputs to `SmartFlowService` while keeping a masked Markdown summary in the chat history for sensitive-looking field names such as `key`, `secret`, `password`, or `token`.

## Live workflow animation in chat

Regular workflow-backed chat requests render a transient
`GnOuGo.Assets.Animation` scene in the active conversation's navigation entry.
The scene follows real workflow telemetry: stable workflow
instances and step occurrences drive walking, roundabout work, parallel clones,
runtime workflow handoffs, parcel completion, failure, and final delivery. It
never runs a second synthetic timer.

The conversation uses a document-style layout: compact right-aligned user
prompts and borderless full-width assistant turns. A single live workflow scene
is hosted directly below the active conversation title in the left navigation
history, while its Activity and visibility controls remain in the related
response action row beside Trace. The scene has the same subtle border as the
rest of the application, a viewport-responsive height capped at 500 px, and
native horizontal and vertical scrollbars. Follow mode is enabled by default
and scrolls only this internal panel. Every submitted message allocates a new,
correlation-keyed animation host before workflow telemetry starts. The previous
host fades, disposes its browser controller, and is replaced by this clean panel
so actors, portals, scene layers, and queued events cannot leak between turns.
No title, lane count, node count, or live-telemetry caption is rendered.

Human Input cards use the same centered 1160 px conversation column, with an
860 px maximum card width. While the workflow waits, its GnOuGo retains the
persistent waiting pose. When the user submits a response, a blue response
capsule enters from beyond the visible animation viewport, arcs toward the
waiting actor, and disappears on receipt before execution resumes.
Resume preserves the already-active scene and the human-step completion uses a
stationary actor rig, avoiding a duplicate scene entrance, reception pose, or
top-to-bottom actor jump.

The empty assistant turn displays three small, borderless black typing dots
from submission through Human Input pauses and other workflow activity.
Response-level messages from nested LLM steps appear above the loader as
preliminary responses and also remain available in Activity. Submitted Human
Input values are listed beside them, with sensitive values masked. These
progress items remain visible with the final response, while only the principal
workflow answer replaces the loader itself. An execution error also dismisses
the loader. The dots are left-aligned with assistant response text.

User turns are rendered as encoded plain text rather than reparsed as Markdown.
This preserves every submitted line break, indentation, and large pasted block
without allowing Markdown extensions such as YAML front matter to hide content.
Assistant turns continue to use the full Markdown pipeline.

The left navigation uses the base `GnOuGo.Assets.Bears` SVG as an inline,
script-free idle animation. Its stable ID prefix prevents SVG definition
collisions with workflow actors. Conversations are ordered newest-first and
grouped using English local-date labels such as **Today**, **Yesterday**,
**The day before yesterday**, and **N days ago**. The compact brand is
**GnOuGo** with the tagline **Simple. Safe. Transparent.**

The top navigation uses a blue **G** wordmark and a **GnOuGo** product trigger.
Its custom dropdown lists the default dynamic workflow and every available
agent while preserving the persisted default-agent selection. A separate
ellipsis menu provides conversation creation and agent-list refresh actions.
Both menus close through a shared click-away backdrop and remain compact on
mobile.

Dynamic planning and routing have dedicated live semantics. `workflow.plan`
walks the main GnOuGo to a planning roundabout, `workflow.route` uses a routing
roundabout, and `workflow.execute` uses a handoff roundabout. When a generated or
selected workflow starts, a caller-aware `workflow.discovered` event announces
the new lane before its GnOuGo spawns and receives the parcel. Short generated
workflows still receive compact leaf roundabouts; source-less runtime work can
append bounded step patches instead of leaving the actor on an anonymous node.

The browser keeps a short presentation queue so very fast real events still
produce visible walking, working, handoff, and delivery motion without delaying
the workflow. A long-running real step repeats a calm action cycle until its
authoritative `step.end` arrives: routing communicates, LLM work types, MCP
work uses its communication pose, and HITL keeps waiting. Controller mounting
is acknowledged before events leave the Blazor queue. The card and message bubbles use the full chat width; the SVG
keeps its complete aspect ratio, has no maximum scene height, and is resized by
a `ResizeObserver` when the application window changes. When a later question
creates another animation card, the chat follows it only after its SVG has
mounted and acquired its final height. Focus events may pan inside their own
card but cannot scroll the conversation back to an older execution.

Thinking and technical progress are kept out of `ChatMessageDto` and local chat
history. They are held in memory for the active execution and displayed from
the card's **Activity** drawer. The existing trace drawer remains separate.
Animation state is also transient and is not restored after a reload.

`human.input` pauses the scene at a human-input counter and keeps the GnOuGo
alive with calm waiting motion. Text, choice, confirmation, and structured
controls appear attached to the live card; submitted values are summarized in
that card instead of becoming chat messages. Timeout, cancellation, and
failure update the same parcel and execution status.

`/api/chat/stream` retains its existing SSE event names and text payloads.
Additional `animation.prepared`, `animation.scene.patch`, and
`animation.event` events use single-line, source-generated JSON payloads.
Workflow YAML and inputs are not included in those animation payloads.

Blazor serializes animation interop through a single guarded queue so
overlapping post-render callbacks cannot remove or reorder live events. The
browser controller records its applied-event count, latest event, pending
queue size, and recoverable error on the scene host for runtime diagnostics.
One malformed visual event is logged and skipped without stopping later
workflow motion.

Flow can reject invalid or missing inputs before its first telemetry span is
opened. In that case Agent.Server emits a short failed startup and delivery
sequence instead of leaving the prepared scene indefinitely at its first
frame. Once workflow telemetry has started, only real workflow and step
signals drive the scene.

The Agent Vite bundle is incrementally rebuilt by normal `dotnet build` and
`dotnet run` builds when Agent, Animation, or Bears frontend runtime sources
change. Set `-p:SkipClientBuild=true` only when a previously built bundle is
known to be current.

## Main routing workflow and conversation history

When no explicit/default agent is selected, `SmartFlowService` runs the embedded `SmartFlow/main-routing-agent.yaml` workflow. That workflow uses `workflow.route` to expand all persisted database agents (`ref: { kind: database }`), select one or more relevant sub-workflows, auto-extract structured inputs from the prompt/history, and request any remaining missing or invalid declared inputs through the existing Human Input form before execution. Candidate forms are presented one at a time, then the completed workflows use their configured execution policy. The route also includes a local general fallback workflow so a fresh installation can still answer prompts before any persisted agents exist.

`/gnougo add` runs generic capability preflight before workflow decomposition. It separates positive external operations from prohibitions and invariants, discovers every configured MCP catalog, resolves operations only to exact advertised capabilities or allowed native Flow steps, and fails before persistence when a required positive operation is unavailable or uncertain. Resolved operations and constraints remain locked through workflow generation. Required resource cleanup is generated under the Flow workflow-level `finally` array.

Read and write capabilities remain discoverable by default; preflight describes availability rather than silently changing an MCP server's execution policy. When preflight fails, the chat response and trace show the sanitized error code, unavailable operation IDs/descriptions, failed catalogs, and a generic configuration action instead of only the summary message.

The Blazor chat session now carries a server-facing `ConversationId`. The UI keeps its local transcript for display, while `SmartFlowService` loads recent server-side messages into the routing workflow as `history` and appends the user/assistant turn after a successful answer. HTTP clients can also pass `conversationId` and `prompt` on `/api/chat` or `/api/chat/stream`; if omitted, the server creates a new conversation id and returns/emits it.

Standalone MCP hosts still expose `/mcp` directly in their own projects:

- `GnOuGo.Agent.Mcp` → `http://127.0.0.1:5198/mcp`
- `GnOuGo.KeyVault.Mcp` → `http://127.0.0.1:5197/mcp`
- `GnOuGo.DocIngestor.Mcp` → `http://127.0.0.1:<port>/mcp`

## Bundled stdio MCP tools

The base `appsettings.json` now enables `GnOuGo.Browser.Mcp`, `GnOuGo.Cmd.Mcp`, `GnOuGo.Document.Mcp`, `GnOuGo.GithubCopilot.Mcp`, and `GnOuGo.Git.Mcp` for non-development runs using bundled executable paths:

```json
{
  "LLM": {
	"McpServers": {
	  "GnOuGo.Browser.Mcp": {
		"Type": "stdio",
		"Command": "tools/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp",
		"Args": []
	  },
	  "GnOuGo.Cmd.Mcp": {
		"Type": "stdio",
		"Command": "tools/GnOuGo.Cmd.Mcp/GnOuGo.Cmd.Mcp",
		"Args": []
	  },
	  "GnOuGo.Document.Mcp": {
		"Type": "stdio",
		"Command": "tools/GnOuGo.Document.Mcp/GnOuGo.Document.Mcp",
		"Args": []
	  },
	  "GnOuGo.GithubCopilot.Mcp": {
		"Type": "stdio",
		"Command": "tools/GnOuGo.GithubCopilot.Mcp/GnOuGo.GithubCopilot.Mcp",
		"Args": []
	  },
	  "GnOuGo.Git.Mcp": {
		"Type": "stdio",
		"Command": "tools/GnOuGo.Git.Mcp/GnOuGo.Git.Mcp",
		"Args": []
	  }
	}
  }
}
```

During local source-based development, `appsettings.Development.json` still overrides these entries to use `dotnet run --project ...`.

Published outputs now bundle the MCP stdio tools under `tools/`:

- `GnOuGo.Agent.Server` publish output includes `tools/GnOuGo.Browser.Mcp/`, `tools/GnOuGo.Cmd.Mcp/`, `tools/GnOuGo.Document.Mcp/`, `tools/GnOuGo.GithubCopilot.Mcp/`, and `tools/GnOuGo.Git.Mcp/`
- `GnOuGo.Agent.Desktop` publish output includes `tools/GnOuGo.Browser.Mcp/`, `tools/GnOuGo.Cmd.Mcp/`, `tools/GnOuGo.Document.Mcp/`, `tools/GnOuGo.GithubCopilot.Mcp/`, and `tools/GnOuGo.Git.Mcp/`

This keeps the browser, command, document, code, and Git MCP servers available in packaged server, desktop, and container runs without requiring the repository source tree.
Final publish outputs also strip all `.pdb` files from both the main application and bundled MCP tools before packaging.

## Server publish (trimmed self-contained)

The linux-x64 CI and Docker paths publish `GnOuGo.Agent.Server` as a trimmed self-contained single-file executable. That path intentionally passes:

- `/p:PublishAot=false`
- `/p:PublishTrimmed=true`
- `/p:PublishSingleFile=true`

The server uses Entity Framework Core for all persistence (Agent, KeyVault, OTLP Collector, Diff, Files). Blazor Interactive Server components are fully included.

The Docker image is built from `mcr.microsoft.com/dotnet/aspnet:10.0` and starts the executable directly:

```powershell
Set-Location "C:\github\GnouGo"
docker build -t gnougo-agent -f src/GnOuGo.Agent.Server/Dockerfile .
docker run --rm -p 5000:5000 gnougo-agent
```

## Desktop publish

The desktop workflow publishes a trimmed self-contained `GnOuGo.Agent.Desktop` (Photino) with bundled stdio MCP tools. The bundled stdio tools (`GnOuGo.Cmd.Mcp`, `GnOuGo.Document.Mcp`, `GnOuGo.Git.Mcp`, `GnOuGo.GithubCopilot.Mcp`, `GnOuGo.DocIngestor.Mcp`) are published as Native AOT executables for maximum startup performance.

## Default Local Data Locations

By default, the GnOuGo workspace remains `Desktop/GnOuGo`.
Persisted agent workflow YAML files are saved in `Desktop/GnOuGo/.GnOuGo/`, uploaded files are saved in `Desktop/GnOuGo/.GnOuGo/Files/`, and SQLite databases are saved in `Desktop/GnOuGo/.GnOuGo/data/`.
The default settings carry these relative paths explicitly:

- agent workflows → `./.GnOuGo/{agent-name}.yaml`
- uploaded files → `./.GnOuGo/Files/`
- `Agent:DatabasePath` → `./.GnOuGo/data/gnougo-agent.db`
- `KeyVault:DatabasePath` → `./.GnOuGo/data/gnougo-keyvault.db`
- `DocsIngestorMcp:DatabasePath` → `./.GnOuGo/data/gnougo-docs-ingestor-mcp.db`
- `DocsIngestorMcp:VectorDatabasePath` → `./.GnOuGo/data/gnougo-docs-ingestor-vectors.sqlite`
- `DocsIngestorMcp:OriginalsDirectory` → `./.GnOuGo/data/docs-ingestor/originals/`
- `Database:Path` (embedded OTLP collector) → `./.GnOuGo/data/gnougo-telemetry.db`

If you explicitly configure an absolute path, that override is preserved.

## Embedded OTLP collector

`GnOuGo.Agent.Server` now embeds `GnOuGo.OtlpCollector.Server` by design, in the same process, but on dedicated telemetry ports.

- Main UI / APIs / mounted MCP endpoints stay on the primary app URL.
- OTLP gRPC ingest listens on `http://127.0.0.1:4317` by default.
- OTLP HTTP ingest + tenant/debug APIs listen on `http://127.0.0.1:4318` by default.

This keeps the collector reusable as an independent component while allowing the local agent and the Desktop host to export logs, traces, and metrics to an in-process collector in real time.

Configuration lives in `appsettings.json`:

```json
{
  "OtlpCollector": {
	"Enabled": true,
	"Host": "127.0.0.1",
	"GrpcPort": 4317,
	"HttpPort": 4318,
	"ExposeHealthEndpoint": true
  },
  "OpenTelemetry": {
	"Enabled": true,
	"Protocol": "Grpc",
	"OtlpEndpoint": "http://127.0.0.1:4317"
  }
}
```

When the embedded collector is enabled, the OpenTelemetry exporters are automatically repointed to the local telemetry listener.

## Per-workflow trace files

`SmartFlowService` can save the complete OpenTelemetry activity tree associated with each execution after its root chat span has stopped. Enable the typed setting in `appsettings.json`:

```json
{
  "WorkflowTraceExport": {
    "Enabled": true
  }
}
```

Each execution produces an indented JSON document under the workspace data directory:

```text
<GnOuGo workspace>/.GnOuGo/traces/DD-MM-YY-HH-mm-ss.json
```

JSON is used instead of plain text so an LLM or another diagnostic tool can reliably inspect the trace metadata, summary, spans, parent/child relationships, semantic attributes, baggage, events, and links. If concurrent workflows finish during the same second, the later filename receives a short trace-id suffix so no trace is overwritten. Capture remains available when OTLP export is disabled; setting `WorkflowTraceExport:Enabled` to `false` disables both capture and file creation.

## OpenTelemetry HTTP visibility

The server already enables both inbound and outbound HTTP span capture when OpenTelemetry is enabled:

- `AddAspNetCoreInstrumentation()` captures incoming HTTP requests handled by `GnOuGo.Agent.Server`
- `AddHttpClientInstrumentation()` captures outbound HTTP calls made by the server runtime

This means the trace detail should include standard HTTP span attributes such as:

- request method
- target URL / route
- response status code
- duration
- trace/span correlation with the rest of the workflow

The embedded OTLP collector is intentionally excluded from self-tracing loops, so collector ingest traffic is filtered out. That prevents recursive telemetry noise while keeping application HTTP calls visible.

If you want to inspect HTTP traces in local runs:

- main application traffic is emitted by `GnOuGo.Agent.Server`
- OTLP gRPC ingest endpoint is `http://127.0.0.1:4317`
- OTLP HTTP + trace/log exploration APIs are available on `http://127.0.0.1:4318`

## Circular logging guard

Embedding the OTLP collector in the same process that generates telemetry creates a potential feedback loop:

1. An application log is captured by `CollectorLoggerProvider` or the OpenTelemetry SDK log exporter.
2. The log is written to the `TelemetryIngestQueue`.
3. `TelemetryBatchWriter` flushes the batch to SQLite via EF Core.
4. EF Core logs the `INSERT INTO log_records` command.
5. Without a guard, step 4's log re-enters step 1, creating infinite growth.

`EmbeddedCollectorLogCategoryFilter` breaks this cycle by suppressing the following log category prefixes from both `CollectorLoggerProvider` and `OpenTelemetryLoggerProvider`:

- `OtlpTenantCollector` — batch writer, EF store, gRPC/HTTP receivers
- `Microsoft.EntityFrameworkCore` — all EF Core database commands
- `Microsoft.AspNetCore.Hosting.Diagnostics` — ASP.NET host-level request logs
- `Microsoft.AspNetCore.Routing.EndpointMiddleware` — endpoint routing logs
- `Grpc.AspNetCore.Server` — gRPC transport logs
- `System.Net.Http.HttpClient` — outbound HTTP (OTLP exporter traffic)

Additionally, `appsettings.json` sets these categories to `Warning` level globally so they do not spam the console output either.

## Frontend (SCSS + offline JS via Vite)

The UI styles and client helpers are bundled with **Vite** into `wwwroot/ui/app.css` and `wwwroot/ui/app.js`.

### Build frontend once

```powershell
Set-Location "C:\github\GnouGo\src\GnOuGo.Agent.Server\ClientApp"
corepack pnpm install --frozen-lockfile
corepack pnpm build
```

### Dev (optional)

```powershell
Set-Location "C:\github\GnouGo\src\GnOuGo.Agent.Server\ClientApp"
corepack pnpm install --frozen-lockfile
corepack pnpm dev
```

## Run the server

```powershell
Set-Location "C:\github\GnouGo\src\GnOuGo.Agent.Server"
dotnet run
```

Default development URLs from `Properties/launchSettings.json`:

- `https://localhost:5001`
- `http://localhost:5000`

Useful endpoints:

- UI: `/`
- chat API: `/api/chat`
- streamed chat API: `/api/chat/stream`
- health: `/health`
- mounted MCP: `/mcp/agent`, `/mcp/keyvault`
- OTLP gRPC collector: `http://127.0.0.1:4317`
- OTLP HTTP collector + trace/log exploration API: `http://127.0.0.1:4318`

> Note: for simplicity, this repo ships with pre-built assets in `wwwroot/assets`.

## Model catalog cache

The server uses `IMemoryCache` to cache provider model listings for a short duration.

- Service: `ILLMModelCatalog`
- Default absolute expiration: `30` seconds
- Configuration section: `ModelCatalogCache`

Example:

```json
{
  "ModelCatalogCache": {
	"Enabled": true,
	"AbsoluteExpirationSeconds": 30
  }
}
```

The cache key includes the active provider configuration fingerprint, so changing a provider URL or credentials invalidates the cached entry automatically.

## MCP capability cache

Workflow execution uses `IMemoryCache` to cache MCP server capability discovery results: tools, prompts, resources, and their descriptions.

- Service: `WorkflowEngine`
- Default sliding expiration: `3600` seconds
- Configuration section: `McpCapabilityCache`

Example:

```json
{
  "McpCapabilityCache": {
	"SlidingExpirationSeconds": 3600
  }
}
```

## Test

```powershell
dotnet test "C:\github\GnouGo\tests\GnOuGo.Agent.Server.Tests\GnOuGo.Agent.Server.Tests.csproj"
```
