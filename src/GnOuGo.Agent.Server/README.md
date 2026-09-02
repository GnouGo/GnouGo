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
	Main --> AgentMcp[GnOuGo.Agent.Mcp tools\n/mcp/agent]
	Main --> KeyVaultMcp[GnOuGo.KeyVault.Mcp tools\n/mcp/keyvault]
	Main --> DocsIngestorMcp[GnOuGo.DocIngestor.Mcp tools\n/mcp/docs-ingestor]

	Main --> OtlpGrpc[Embedded OTLP gRPC\nhttp://127.0.0.1:4317]
	Main --> OtlpHttp[Embedded OTLP HTTP + tenant/debug APIs\nhttp://127.0.0.1:4318]
```

#### Single application listener

- The **public application port** is the main `GnOuGo.Agent.Server` listener, for example `http://127.0.0.1:58443`.
- The mounted MCP routes that the UI and runtime services should use are:
  - `http://127.0.0.1:58443/mcp/agent`
  - `http://127.0.0.1:58443/mcp/keyvault`
  - `http://127.0.0.1:58443/mcp/docs-ingestor`
- All three routes are mapped directly by the main ASP.NET Core host. No private MCP loopback listeners, reverse proxy, or helper-host lifecycle is created.
- The embedded OTLP listeners on ports `4317` and `4318` are unchanged.

## Mounted MCP endpoints

`GnOuGo.Agent.Server` hosts the local MCP HTTP services in-process and mounts them on dedicated routes:

- `GnOuGo.Agent.Mcp` → `/mcp/agent`
- `GnOuGo.KeyVault.Mcp` → `/mcp/keyvault`
- `GnOuGo.DocIngestor.Mcp` → `/mcp/docs-ingestor`

All three mounted services use the stable C# MCP SDK `2.2.0` and one stateless Streamable HTTP transport. Each route carries its logical server identity as endpoint metadata. The SDK's request-local `ConfigureSessionOptions` hook sets the exact server name/version and replaces the request's tool collection with only tools marked for that route; missing or unknown metadata fails closed. Because the options are request-local, concurrent discovery cannot leak one route's catalog into another.

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

After startup, the server replaces port `0` with the actual bound local address and republishes those URLs through the runtime MCP configuration store before running the existing Agent user-configuration bootstrap. Runtime consumers therefore use the same listener as the Blazor UI and Minimal APIs.

`GnOuGo.Agent.Server` also uses the mounted `GnOuGo.Agent.Mcp` endpoint as the persistence API for local user defaults:

- `user_config_get` — hydrate persisted `default_llm_provider`, `default_llm_model`, and `default_agent`
- `user_config_set` — save updated defaults after `/llm default`, `/llm add` auto-promotion, or `/gnougo select`

The persisted values live in the Agent MCP SQLite database (`Agent:DatabasePath`) rather than only in browser state.
LLM provider and MCP server definitions are hydrated from encrypted KeyVault secrets; `user-settings.json` is no longer used. Each workflow execution builds its MCP runtime catalog from the latest KeyVault-backed definitions, so a server saved through `/mcp add` is available to the next `/gnougo add` or workflow run without restarting Agent.Server. `/mcp list` and workflow capability preflight therefore use the same persisted configuration source.

Configuration logical names are case-insensitive across LLM providers, MCP servers, embedding configurations, and embedding defaults. Save operations reuse an existing canonical key and retire every equivalent case or legacy alias only after the replacement value is stored; remove operations retire all aliases for the logical configuration. A single canonical key takes precedence over a single legacy key. Multiple same-priority canonical or legacy keys for the same logical name are rejected as ambiguous without logging their values. This keeps direct KeyVault readers and Agent runtime hydration on the same deterministic configuration identity.

For user-configured HTTP servers, `/mcp edit <name>` can keep the current authentication settings, rotate an API key, replace OIDC client credentials, switch between `api_key`, `oidc`, and `none`, or remove authentication. Choosing `keep_current` preserves the encrypted credentials; choosing a named authentication mode collects replacement credentials and clears fields belonging to the previous mode. Bundled MCP servers continue to expose only their allow-listed override fields, and stdio MCP servers do not use this HTTP authentication flow.

`/llm add` can configure `openai`, `ollama`, `copilot`, and `anthropic` providers. The Anthropic provider uses the Anthropic Messages API endpoint `https://api.anthropic.com/v1` and stores the API key encrypted in KeyVault like the other remote providers. The legacy `claude` name is still accepted as an alias for existing configurations.

The built-in `local` provider runs Qwen3 directly inside Agent.Server through
LLamaSharp; it needs no API key, HTTP model server, Python, Docker, or child
process. The model is deliberately not bundled in release archives. Manage the
pinned and checksum-verified catalog with:

```text
/models list
/models install qwen3:0.6b
/models remove qwen3:0.6b
/models remove qwen3:0.6b --force
```

An install preserves an existing usable default. If the current default is absent
or incompletely authenticated (including the untouched empty OpenAI placeholder),
Agent persists `local / qwen3:0.6b` through its tenant-aware Agent MCP configuration
boundary. Removing the active default requires `--force`. `/llm list` includes the
built-in model and `/llm default local qwen3:0.6b` selects an installed model.

The immutable catalog entry is Apache-2.0
`Qwen3-0.6B-Q4_0.gguf`, revision
`a41486f827d17edd055fe6b3b0ba3f8d427c0519`, size `428,970,080`, and
SHA-256 `da2572f16c06133561ce56accaa822216f2391ef4d37fba427801cd6736417d4`.
Downloads are resumable and complete atomically only after exact size and checksum
verification.

After model selection, `/llm add` and `/llm edit <provider>` always display an editable review form containing token limits, pricing, structured-output/tool/JSON support, temperature and reasoning behavior, vision/audio/embedding support, supported reasoning efforts, and unsupported request parameters. Exact catalog values remain catalog-backed when accepted unchanged. Edited values and accepted fuzzy/heuristic defaults are persisted as non-secret, provider-qualified overrides such as `openai/model-id`; legacy unqualified overrides remain readable. Approximate matches are restricted to the normalized provider and the UI identifies the source model and similarity before saving.

Bundled MCP servers can also expose selected editable fields through the `BundledMcp` settings section. Each field maps to a KeyVault secret and a runtime target such as `env:Git__Token`, so `/mcp list` can show the bundled server and `/mcp edit <name>` only prompts for the configured fields. The default configuration makes `GnOuGo.Git.Mcp` listable and exposes only the Git token; the token is saved encrypted in KeyVault and injected into the Git MCP process as `Git:Token` when runtime MCP options are hydrated.

`GnOuGo.GithubCopilot.Mcp` is also a listable bundled server. `/mcp edit GnOuGo.GithubCopilot.Mcp` exposes its provider override, fallback model, reasoning effort, logged-in-user mode, request timeout, managed-session TTL, broad-approval gate, and reusable sandbox-bypass gate. Provider choices come from configured KeyVault LLM providers. Values are validated and stored as separate encrypted `LLM--McpServerOverrides--GnOuGo.GithubCopilot.Mcp--...` entries, while per-field inheritance deletes the selected override. Enabling reusable sandbox bypass automatically enables broad approvals through generic editable-field dependency metadata. The bundled definition is marked with `ReadsKeyVaultDirectly`, so runtime hydration propagates only `KeyVault__DatabasePath`, including a custom path, and never injects decrypted `Code__Copilot__...` values. The MCP retrieves and maps its own configuration through the generic `GnOuGo.KeyVault.Core` abstractions. Credentials stay in their existing `LLM--Models--<provider>` entries, and changes affect the next workflow/MCP process rather than an already-running session.

## Dynamic workflow input composer

The Blazor chat composer resolves the active/default agent workflow through `SmartFlowService` and adapts the user input area to the workflow `inputs` declaration:

- prompt-like workflows keep the compact single textarea when there is one required `task`/`prompt`/`query`/`request`/`input`/`message` string input, with optional defaulted inputs hidden;
- workflows with multiple required inputs render one field per top-level input;
- object inputs with declared `properties` render nested fields;
- `array`, `object`, `dictionary`, and `any` fields accept JSON or YAML text;
- the UI sends structured `JsonObject` workflow inputs to `SmartFlowService` while keeping a masked Markdown summary in the chat history for sensitive-looking field names such as `key`, `secret`, `password`, or `token`.

## Live workflow animation in chat

Every submitted chat request renders a correlation-owned
`GnOuGo.Assets.Animation` scene inside its assistant response, including
workflow/agent creation and slash commands. The **Traces**, **Animation**, and
**Activity** controls remain available even when a response has no text or
ends in an error. The scene follows real workflow telemetry: stable workflow
instances and step occurrences drive walking, roundabout work, parallel clones,
runtime workflow handoffs, parcel completion, failure, and final delivery. It
never runs a second timer-driven preview. Agent creation/management commands
reuse their native workflow telemetry and preserve child-workflow identities.
Direct slash commands without a workflow use a small synthetic command
lifecycle so their waiting, success, and failure states have the same visual
contract as workflow-backed messages.

The conversation uses a document-style layout: compact right-aligned user
prompts and borderless full-width assistant turns. Each response keeps its own
full-width animation panel, with a viewport-responsive height capped at 620 px
and native horizontal and vertical scrollbars. Follow mode is enabled by
default and scrolls only that message's panel. During a walk, the camera leads
only partway toward the next station so GnOuGo remains visibly in motion; the
step event then completes the centering. Switching conversations disposes
detached browser controllers; returning to an in-memory conversation remounts
the SVG and replays its ordered scene patches/events so actors, portals, scene
layers, and queued events cannot leak between turns.

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
walks the main GnOuGo to a planning roundabout and keeps the `think` action for
the complete running step, `workflow.route` uses a routing
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
is acknowledged before events leave the Blazor queue. Every patch and event
also verifies that the controller still owns the currently connected Blazor
host. If streaming replaced that DOM branch, the stale controller is rejected
and the current panel is remounted from its authoritative event history instead
of silently animating a detached SVG. The card and message bubbles use the full
chat width; the SVG keeps its complete aspect ratio, has no maximum scene
height, and is resized by a `ResizeObserver` when the application window
changes. When a later question
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
failure update the same parcel and execution status. Confirmation buttons use
their labels only for presentation and submit a Boolean response; Flow.Core also
normalizes provider labels such as `approve` and `reject` before expressions run.
MCP elicitation uses this same visible card and wire payload. In particular,
interactive Copilot permission requests emit the waiting animation before the
form is streamed, and emit a resumed/refused/cancelled signal afterward. The
request is correlated to its exact workflow run and MCP step rather than read
from a shared pending-request queue, so concurrent executions cannot steal one
another's permission form.

When the Copilot host gate `Code__Copilot__EnableApproveAll=true` is enabled,
permission cards distinguish **Allow similar operations for this task**,
**Allow all for this Copilot task**, **Allow all for this workflow run**, and
**Allow all future runs for this agent**. Persistent approval requires a second
confirmation. It is keyed by tenant and stable agent ID, survives restarts and
renames, and is revoked automatically when the agent is deleted. Use
`/mcp copilot permissions` to list persistent grants and
`/mcp copilot permissions revoke <grant-id>` to revoke one manually. When
`Code__Copilot__EnableSandboxBypassGrants=true` is also enabled, a sandbox-bypass
request offers separate task, workflow, and future-agent broad choices. The
future-agent choice requires a second warning confirmation. Ordinary broad grants
still exclude bypass. Automatically approved operations remain visible in
**LIVE WORKFLOW ACTIVITY** and never leave a stale human-input card.

`/api/chat/stream` retains its existing SSE event names and text payloads.
Additional `animation.prepared`, `animation.scene.patch`, and
`animation.event` events use single-line, source-generated JSON payloads.
Workflow YAML and inputs are not included in those animation payloads.

Blazor serializes animation interop through a single guarded queue so
overlapping post-render callbacks cannot remove or reorder live events across
message-owned panels. Mutable conversation and execution collections are
snapshotted before any JavaScript interop await, preventing streaming updates
from invalidating an active renderer enumeration and terminating the Blazor
circuit. Error statuses are normalized and a terminal failed event
is added when a response-level failure has no matching workflow terminal event.
On a real workflow failure, the actor stays at the highlighted crashing step
in a single terminal failure pose; no successful return-to-delivery
choreography is queued, so the panel does not blink between failure and walking.
The browser controller records its applied-event count, latest event, pending
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

`/gnougo add` first runs generic up-front intent clarification, then inventory-first capability preflight and workflow decomposition, using medium reasoning. The clarification analyst sees only the raw request and caller context, asks up to five material questions in one rich form, and may use one later follow-up form within a shared eight-question budget. Each question shows two or three described answers with the AI recommendation first, badged, and preselected; the user can select Other and type a custom answer or abandon planning. Questions follow the request language, while fixed controls use the Agent UI culture. Capability and extraction stages may ask the remaining form only for validated user-intent ambiguity; malformed model output, unavailable tools, and runtime-dependent decisions never become human questions. Once inferred preflight succeeds, decomposition reuses that strict structured-output proof so immutable operation/catalog ownership and shared artifact materializers remain attached to their declared data-flow roots.

After clarification, the first capability structured call inventories runtime operations and constraints without seeing tools. `required` means the generated plan must implement an obligation, including every requested conditional branch; only explicitly optional enrichment may be omitted, and that classification requires exact request/context evidence. A compact paged selector then chooses relevant physical MCP tools from one entry per tool, adds MCP-declared artifact producers and complete-operation wrappers, and expands authoritative schemas and selector variants only for that selected set before final matching. If inventory or required-candidate selection is incomplete, each stage gets one bounded repair. Complete discovery remains available to dry runs and deterministic validation, and the 256,000-character expanded-catalog guard is unchanged. Documented scalar selectors make logical variants of a multi-action MCP tool distinct and lock their literal request values through decomposition and YAML validation. Mutually exclusive runtime outcomes are locked as one exact-value switch rather than reported as creation-time ambiguity. A requested uncertain outcome is represented by an explicit non-mutating switch case. When the selected decision producer has no usable enum output, planning may impose a strict provider-neutral `/json/decision` structured-output contract whose enum covers every effect branch and the no-effect value. The owning analysis leaf is now validated and repaired before parent assembly: it must contain one locked producer and expose that exact enum output unchanged, without custom-function normalization or fallback. When that value crosses leaf boundaries, Flow canonicalizes the producer output and consumer inputs to the same enum and recomputes validation diagnostics after each bounded extraction repair. The parent calls the leaf that owns the conditional switch once instead of duplicating it across cases. Host configuration, internal provider/credential resolution, and the outer agent-persistence action are outside the generated workflow inventory. Required resource cleanup is generated under the Flow workflow-level `finally` array.

All `/gnougo add` planning phases request provider-managed background execution. OpenAI providers
use the configured `RequestPolicy.BackgroundProtocol`: `Auto` probes Responses,
`ChatCompletions` bypasses it, and `Responses` requires it. In `Auto`, deterministic route-level
`404`, `405`, or `501` incompatibility is cached immediately, so a transient failure in the first
Chat fallback does not repeat a known-invalid Responses probe. Request-specific errors are not
cached; ambiguous `400`/`422` compatibility is cached only after Chat succeeds. A compatible
endpoint that specifically rejects `max_completion_tokens` still receives one payload-compatible
retry omitting only that field. Strict structured output, reasoning effort, tools, model selection,
authentication, and API version remain unchanged.

Model metadata `MaxOutputTokens` is a ceiling rather than a request default. With the default
`UnspecifiedOutputTokens: Omit`, an unspecified workflow allowance sends no output-token field;
explicit limits are clamped to the model ceiling and optional provider cap. The credential probe
explicitly requests at most 64 tokens. Provider policy survives runtime snapshots and encrypted
credential overlays, while malformed policy combinations fail startup.

HTTP recovery makes at most four attempts for `425`, `429`, `500`, `502`, `503`, and `504`, honors
valid `Retry-After`, and otherwise uses bounded full-jitter exponential backoff. Quota/billing and
authentication/authorization envelopes are terminal even when carried as `429`; transport errors,
timeouts, cancellation, and other `4xx` are not replayed. `LLM_TIMEOUT` and `LLM_NETWORK` remain
retryable workflow codes, while request/configuration rejections use `LLM_PROVIDER`. The workflow
failure presentation includes only sanitized classification, HTTP status, actual attempts,
exhaustion, accepted `Retry-After`, provider-safe code, and recommended action. It never renders
the endpoint, response body, prompt, credential, client identity, or scope. This technical recovery
does not consume the human-input budget. User-requested cancellation remains `CANCELLED`.

Read and write capabilities remain discoverable by default; preflight describes availability rather than silently changing an MCP server's execution policy. When preflight fails, the chat response and trace show the sanitized error code, unavailable operation IDs/descriptions, and failed catalogs instead of only the summary message. Catalog discovery failures direct the operator to restore MCP startup, connectivity, or configuration (or remove the catalog); missing capabilities retain separate capability-oriented recovery guidance.

In inferred mode, matching is reported per operation as `matched`, `composed`, `conditional`, `local`, `ambiguous`, `contract_gap`, or `unavailable`. Operations explicitly classify their execution/effect kind, exact upstream `input_operation_ids`, and any runtime decision source; the declared edges let a local validation be traced to its external producer without provider-specific inference. Constraints explicitly classify exact denials versus structural policies, so Flow does not guess those semantics from GitHub-specific wording. Repeated exact catalog IDs are deduplicated, and native-only constraint candidates remain orchestration policy because exact denied alternatives are an MCP-only contract. Before the one bounded repair, Flow removes a redundant whole-tool card when one final match also contains exactly one bound variant of that physical tool, without comparing selector values to request wording. It then recursively closes declared artifact dependencies: a unique minimal acyclic chain such as materializer → comparison payload → consumer is composed automatically, while multiple, missing, cyclic, and over-limit graphs remain explicit fail-closed outcomes. If a conditional branch set is incorrectly locked to an upstream authorization gate whose contract cannot cover the branch values, Flow searches only its declared upstream operation edges and all inputs of local aliases. It prefers one complete typed enum contract, otherwise one structured semantic root of the artifact composition, excluding technical materializers; among several declared artifact consumers, only a sole maximal required-artifact-kind superset is canonical. Equal or incomparable roots remain ambiguous. The confirmation operation and write ordering remain independent. Unique recovery emits `conditional_decision_source_canonicalized`. Multiple or absent compatible sources emit `conditional_decision_source_ambiguous` or `conditional_decision_source_unavailable` as technical gaps and never open HITL. If one recovered structured producer owns several independent decisions, each receives a distinct stable field derived from opaque operation identity and emits `conditional_decision_output_path_canonicalized`; incompatible enum contracts therefore cannot overwrite one another during extraction. Conditional matching also distinguishes mutually exclusive selector alternatives from a provider-neutral ordered composition: when the locked operation explicitly permits no effect, the matcher may declare that every structurally distinct selected capability runs once, in catalog order, inside one guarded effect case. Every no-effect branch must carry exact task evidence overlapping that operation's structural requirement, so availability, permission, safety-boundary, and ordinary failure policies cannot manufacture an abstention branch. Flow validates the matching mode, exact order, no-effect case, and non-mutating default without deriving lifecycle semantics from tool or provider names. A one-capability conditional remains malformed. Coverage requirements distinguish intrinsic `capability_contract` behavior from `workflow_structure` guarantees such as cardinality, complete-scope iteration, ordering, finalization, failure handling, quality thresholds, runtime argument values or instructions, input locator representation, and locally derivable parameter mapping. Only the intrinsic primitive is compared with static tool cards even when an evidence excerpt retains structural context; structural guarantees remain locked for workflow generation and validation. If an evidence-qualified reviewer still reports a gap, one bounded provider-neutral adjudication must prove whether the primitive itself is missing or only controlled workflow-structure facets differ. Structural-only differences are canonicalized automatically with sanitized reason `capability_coverage_workflow_structure_canonicalized`; malformed adjudication is a model-contract violation and never opens HITL. One bounded repair can resolve remaining malformed, unknown-ID, ambiguous, initially unavailable, or confirmed intrinsic-gap decisions while preserving already valid unrelated matches; an ungrounded conditional write also unlocks its declared upstream decision chain so both sides can be repaired together. The coverage review and adjudication see only selected cards, lock allowed IDs in their schemas, accept harmless Unicode/whitespace normalization in copied evidence, record precise sanitized contract issue codes, and retry invalid contracts once. Structured pipeline extraction independently declares exact `owned_operation_ids` for each leaf. A unique owner lets Flow restore omitted exact planned-tool occurrences from the locked contract; unknown or duplicate owners fail closed without description, provider/tool-name, catalog-number, or leaf-order matching. If only genuine user-intent ambiguity remains, `/gnougo add` presents one batched form with questions about observable behavior, scope, runtime rules, external-effect boundaries, success criteria, and failure behavior, then reruns the complete inference sequence once. It never asks the user to repair match cardinality, select catalog IDs, assemble technical producer chains, distinguish capability primitives from workflow structure, or repair extraction ownership. A valid review and adjudication that establish only weaker intrinsic documented behavior may use the remaining shared form budget to ask whether that exact relaxation is acceptable; preserving the original behavior stops safely. Missing input, timeout, malformed output, unavailable capabilities, or another unresolved result fails closed as `cannot_plan_safely` or `unsupported`, with an action to clarify/abandon, retry/change the planning model, or revise/configure capabilities. Repeated malformed matching, including a one-branch conditional selector set, is classified as `model_contract_violation`; a remaining decision-contract gap is `unsupported` with an action to configure a typed decision contract or enable structured projection. Traces include sanitized matching normalization and artifact-closure reason codes alongside conditional-grounding and coverage contract events. Remaining failures include sanitized `matching_issues` or `contract_issues`; raw clarification answers, full schemas, prompts, repository content, credentials, and model reasoning are not included.

External writes inferred from a short intention are classified separately from reads and AI execution. The structured inventory declares external-write confirmation as `required`, `forbidden`, or `unspecified`, with exact request/context evidence required for either explicit choice. `/gnougo add` receives a locked platform confirmation operation and ordering policy unless the validated policy is `forbidden`; `unspecified` remains fail-safe. This is language-neutral, so an explicit instruction such as “Aucune confirmation humaine” is honored without provider- or locale-specific keyword rules. A conditional rule such as “only after confirmation” never becomes a document-wide denied tool, while unconditional prohibitions still reject exact denied calls.

The focused live `/gnougo add` smoke test uses the current KeyVault-backed runtime, requires successful discovery of every configured catalog, generates and compiles a provider-neutral fixed-output workflow with no requested external effect, verifies persistence, then deletes the unique temporary agent and restores the previous default-agent setting:

```bash
GNOU_GO_LIVE_AGENT_ADD_SMOKE=1 dotnet test \
  tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj \
  --filter 'FullyQualifiedName~LiveAgentAddSmokeTests.GenericNoExternalEffectIntent_GeneratesValidPersistedAgent'
```

The intention-first live acceptance harness is opt-in because it uses the configured KeyVault-backed provider and external MCP servers. It requires a dedicated validation project with a provider-side USD 25 hard limit. The two environment variables below are an explicit operator attestation; the harness does not modify or query provider billing configuration.

```powershell
$env:GNOU_GO_LIVE_INTENT_AGENT_E2E = "1"
$env:GNOU_GO_LIVE_INTENT_AGENT_PROVIDER_PROJECT_ISOLATED = "1"
$env:GNOU_GO_LIVE_INTENT_AGENT_PROVIDER_HARD_LIMIT_USD = "25"
$env:GNOU_GO_LIVE_INTENT_AGENT_BUDGET_STATE_PATH = Join-Path $env:TEMP "gnougo-live-intent-budget.json"

# Gate 1: provider probe plus one diagnostic generation.
$env:GNOU_GO_LIVE_INTENT_AGENT_GENERATIONS = "1"
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj `
  --filter "FullyQualifiedName~LiveIntentAgentGenerationTests.SimpleIntent_GeneratesThreeValidatedAgentsUsingLiveConfiguration"

# Gate 2: only after Gate 1 succeeds, consume the remaining shared budget.
Remove-Item Env:GNOU_GO_LIVE_INTENT_AGENT_GENERATIONS
dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj `
  --filter "FullyQualifiedName~LiveIntentAgentGenerationTests.SimpleIntent_GeneratesThreeValidatedAgentsUsingLiveConfiguration"
```

The first process performs one minimal provider probe and one diagnostic generation. The second process requires the successful redacted ledger from that gate and performs three independent generations. Together they share 120 LLM calls, five million tokens, 120 minutes, and USD 25 estimated cost. Runtime HTTP recovery is the only automatic provider retry layer; a persistent rate limit or any terminal/deterministic failure stops the live cycle instead of starting another planning call. The ledger stores only cumulative usage and phase flags, and is removed after any failed phase or after final acceptance.

The harness uses a unique temporary SQLite database for its disabled embedded telemetry store and removes the database plus WAL/SHM sidecars during cleanup. When `GNOU_GO_LIVE_INTENT_AGENT_PROGRESS_PATH` is set, typed provider failures record only the provider-neutral classification, HTTP status, actual attempt count, exhaustion flag, accepted retry delay, and provider-safe error code in addition to budget counters. Endpoints, prompts, models, response bodies, and credentials are never written to the progress log.

The harness submits the same short user intention and simulates the human for initial inputs, every up-front or follow-up clarification form, generated-YAML approval, publication rejection, and the scoped confirmation for the disposable fixture. It exercises both an AI-recommended answer and a native custom answer, validates every discovered MCP call and literal selector, executes a read-only review against the configured public acceptance PR while denying publication, and exercises the confirmed write path only against a disposable draft fixture. Unexpected human prompts fail the test. It restores the previous default-agent setting and removes the fixture PR/branch, generated agents, isolated workspaces, and final budget ledger in `finally`.

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

Normal `GnOuGo.Agent.Server` and `GnOuGo.Agent.Desktop` builds stage framework-dependent apphosts for these servers under their own `bin/<Configuration>/net10.0/tools/` directories. `appsettings.Development.json` only extends discovery and call timeouts and continues to use these clean direct commands; it never places `dotnet run` on the MCP stdout transport.

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

The publish retains the LLamaSharp linux-x64 CPU native assets beside the
single-file host; CI explicitly verifies these files.

The server uses Entity Framework Core for all persistence (Agent, KeyVault, OTLP Collector, Diff, Files). That EF Core + SQLite boundary is mandatory: publish-warning work must retain the existing contexts, repositories, schemas, tenant behavior, and compiled models where the component has one. Blazor Interactive Server components are fully included. Verified framework-only trim diagnostics are isolated to these publish profiles; compile-time trim analysis stays enabled.

Run the repository publish verification for the current platform, or audit the exact package/framework warning boundaries with their normal suppressions disabled:

```powershell
pwsh scripts/verify-warning-free-publishes.ps1 -RuntimeIdentifier win-x64
pwsh scripts/verify-warning-free-publishes.ps1 -RuntimeIdentifier win-x64 -AuditKnownTrimWarnings
```

The Docker image is built from `mcr.microsoft.com/dotnet/aspnet:10.0` and starts the executable directly:

```powershell
Set-Location "C:\github\GnouGo"
docker build -t gnougo-agent -f src/GnOuGo.Agent.Server/Dockerfile .
docker run --rm -p 5000:5000 gnougo-agent
```

## Desktop publish

The desktop workflow publishes a trimmed self-contained `GnOuGo.Agent.Desktop` (Photino) with bundled stdio MCP tools. The bundled stdio tools (`GnOuGo.Cmd.Mcp`, `GnOuGo.Document.Mcp`, `GnOuGo.Git.Mcp`, `GnOuGo.GithubCopilot.Mcp`, `GnOuGo.DocIngestor.Mcp`) are published as Native AOT executables for maximum startup performance.

All six desktop RIDs include their LLamaSharp CPU native assets: `linux-x64`,
`linux-arm64`, `win-x64`, `win-arm64`, `osx-arm64`, and `osx-x64`. macOS ARM64
automatically uses Metal layers; Windows and Linux use CPU execution. CUDA and
Vulkan acceleration remain follow-up work.

## Default Local Data Locations

By default, the GnOuGo workspace remains `Desktop/GnOuGo`.
Persisted agent workflow YAML files are saved in `Desktop/GnOuGo/.GnOuGo/`, uploaded files are saved in `Desktop/GnOuGo/.GnOuGo/Files/`, and SQLite databases are saved in `Desktop/GnOuGo/.GnOuGo/data/`.
Workflow-owned working directories are separate and visible under `Desktop/GnOuGo/workflows/`, using purpose-specific children such as `workflows/github-review-123`. Every Agent Server `workflow.plan` entrypoint instructs generated workflows to use this visible root and never `.GnOuGo/` for project or file paths. The `.GnOuGo/` subtree remains reserved for GnOuGo-managed internal state.
The default settings carry these relative paths explicitly:

- agent workflows → `./.GnOuGo/{agent-name}.yaml`
- uploaded files → `./.GnOuGo/Files/`
- `Agent:DatabasePath` → `./.GnOuGo/data/gnougo-agent.db`
- `KeyVault:DatabasePath` → `./.GnOuGo/data/gnougo-keyvault.db`
- `DocsIngestorMcp:DatabasePath` → `./.GnOuGo/data/gnougo-docs-ingestor-mcp.db`
- `DocsIngestorMcp:VectorDatabasePath` → `./.GnOuGo/data/gnougo-docs-ingestor-vectors.sqlite`
- `DocsIngestorMcp:OriginalsDirectory` → `./.GnOuGo/data/docs-ingestor/originals/`
- `Database:Path` (embedded OTLP collector) → `./.GnOuGo/data/gnougo-telemetry.db`
- local GGUF models → `./.GnOuGo/models/`

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

Tool schemas are still discovered once on every newly created live MCP client.
This per-session initialization is required for SDK transport annotations such
as `x-mcp-header`; the shared cache cannot replace it. Consequently request
fields such as GitHub `owner` and `repo` continue to be emitted as
`Mcp-Param-*` headers after a runtime has been rebuilt.

When an agent run offers **Improve**, the failure details carry the deepest
failing local workflow and step. The repair planner is structurally locked to
that location: it may update the failed step and existing direct consumers,
but it cannot remove or rename sub-workflows, `workflow.call` edges, steps,
branches, skills, or public contracts. Any broad rewrite is rejected and
reprompted up to three times before the original workflow is left untouched.

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
