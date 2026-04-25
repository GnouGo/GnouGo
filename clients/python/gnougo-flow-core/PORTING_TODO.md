# Porting TODO — `gnougo-flow-core` ↔ `GnOuGo.Flow.Core`

> **Source of truth:** `src/GnOuGo.Flow.Core/` (.NET).
> **Target:** `clients/python/gnougo-flow-core/src/gnougo_flow_core/` (Python 3.10+).
>
> This document tracks every behavioral gap between the two libraries, ordered
> in the recommended implementation phases. When the .NET reference changes,
> update both the .NET code **and** this list (or open a follow-up item).

## Status legend

- ✅ Done (parity)
- ⚠️ Partial / divergent
- ❌ Missing

---

## Recently shipped (Apr 2026)

- ✅ `WorkflowDocument.version` (with `dsl` alias, both at the model and the
  parser). Replaces the previous `dsl` field.
- ✅ `LLMRequest.reasoning` field.
- ✅ `LlmCallExecutor` reads `input["reasoning"]` and forwards it; emits
  `gen_ai.request.reasoning_effort` telemetry attribute.
- ✅ `WorkflowPlanExecutor` defaults `generator.reasoning` to `"high"` and
  passes it on every LLM request (main attempt + MCP prefilter).
- ✅ New helper module `gnougo_flow_core.reasoning` with
  `normalize_openai_reasoning` / `normalize_ollama_think` (mirrors
  `ChatRequestBuilder` in `GnOuGo.AI.Core`).
- ✅ Tests `tests/test_reasoning_field.py` (33 cases).
- ✅ Phase 1 parser / validator parity slice: required version handling,
  schema validation, nested output expression validation, export validation,
  and `JsonSchemaConverter` port.
- ✅ `WorkflowCheckpoint`, `IWorkflowCheckpointer`, and
  `InMemoryWorkflowCheckpointer` scaffolding added on the Python side.
- ✅ **Phase 2** — Replaced the `eval()`-based expression evaluator and the
  regex-based `ScriptSandbox` by a hand-written AST-based JS-subset
  interpreter (`gnougo_flow_core/_jsmini.py`, no third-party deps). Adds
  optional chaining `?.`, nullish coalescing `??`, ternary, typeof,
  array/object literals, multi-statement WFScript bodies (`var/let/const`,
  `if/else`, `return`), execution limits (max statements, max nodes, 5s
  wall-clock timeout, max call depth), brace-balanced `${...}` scanner,
  built-ins exposed inside the WFScript scope, .NET `formatDate` token
  parity (`yyyy/MM/dd HH:mm:ss/fff`), `WebUtility.HtmlEncode`-equivalent
  Mustache escaping (numeric entities for non-ASCII, single quote left
  alone) and invariant-culture numeric formatting. New tests:
  `tests/test_jsmini.py`, `tests/test_format_date.py`,
  `tests/test_templating.py`, `tests/test_scripting.py`.
- ✅ **Phase 3** — Runtime polish & simple executors. `WorkflowEngine` now logs
  start/end/cancel/fail and per-step start/success/fail through
  `logging.getLogger("gnougo_flow_core")` (overridable via `engine.logger`).
  `execute_async(..., ct: asyncio.Event | None = None)` propagates
  cancellation through every nested `execute_steps_async` call (sequence,
  switch, loop.*, parallel, workflow.call, workflow.execute) and through the
  retry sleep. When `limits.log_step_content` is true the engine emits
  `gnougo-flow.step.input` / `gnougo-flow.step.output` events with
  `gnougo-flow.step.id|type|call_depth|content.*` attributes (mirroring
  `WorkflowEngine.cs:391–424`). `set` deep-clones its resolved input;
  `template.render` now routes through `engine.template_engine` when set,
  validates `mode ∈ {text|json|markdown|html}`, and exposes
  `meta.engine` + `meta.mode`; `switch` matches .NET semantics
  (string-equality via `JsonNode.ToJsonString`, Form A only when both
  expr and case `value` are non-null, Form B fallthrough, error message
  `tests/test_runtime_logging.py`,
  `tests/test_cancellation.py`, `tests/test_template_render_executor.py`,
  `tests/test_step_content_events.py`.
- ✅ **Phase 4** — Loops, parallel, workflow.call. `parallel` raises
  `PARALLEL_LIMIT` with counts and deep-clones each branch's data so loop
  variables propagate (parity with `JsonNode.DeepClone`). `loop.sequential`
  now supports `times`/`while`/`over` (the latter being a Python-only
  extension), honors `item_var` / `index_var`, and rejects mutually
  exclusive combinations. `loop.parallel` enforces *both* `LOOP_LIMIT`
  (against `max_loop_iterations`) and `PARALLEL_LIMIT` (against
  `max_parallel_branches`), deep-clones per-iteration data, and strips
  internal `__*__` keys. `workflow.call` enforces `MaxCallDepth` first,
  deep-copies `args`/`env`, validates `FetchPolicy` (HTTPS scheme via
  `urllib.parse.urlparse`, `allowed_hostnames` allow-list,
  `require_integrity`, post-fetch `max_size_bytes`), supports remote
  `export` selection (with the same precedence as .NET:
  `export → entrypoint → single → first`), and surfaces network failures
  as `WORKFLOW_FETCH_NETWORK`. New tests:
  `tests/test_parallel_executor.py`,
  `tests/test_loop_sequential_executor.py`,
  `tests/test_loop_parallel_executor.py`,
  `tests/test_workflow_call_executor.py`.
- ✅ **Phase 5** — LLM/MCP/human executors + integrations. Added
  `gnougo_flow_core.mcp_cache.McpCacheHelper` (5-minute sliding TTL,
  per-server tools/resources/prompts keys, deep-copy get/set) and
  `gnougo_flow_core.integrations` with `InMemoryMcpClientFactory`,
  `MockMcpServerConfig`, `ConfiguredMcpClientFactory`, `McpSessionAdapter`,
  `RoutingLLMClientAdapter`, argument conversion, and unexpected server-exit
  detection. `mcp.list` now validates wildcard usage, de-duplicates explicit
  servers, returns only requested flattened arrays, uses the cache, and treats
  unsupported `resources/list` / `prompts/list` as empty capability lists.
  `mcp.call` validates `method`/`methods`, wraps timeout/call failures like
  .NET, uses cached auto-discovery, supports prompt fallback for unsupported
  `prompts/list`, builds prompt argument schemas, uses unique internal tool
  names, and aligns the LLM selection/finalization prompts. `human.input`
  parses structured `fields`, supports `timeout_ms: 0`, emits
  `gnougo-flow.step.waiting_for_human` and "Human input received" telemetry,
  and uses a stable run id. New tests: `tests/test_mcp_call_llm_selection.py`,
  `tests/test_mcp_factory.py`, `tests/test_phase5_mcp_human.py`.

---

## A. Models

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| A1 | ✅ | `WorkflowDocument.version` + `dsl` alias | `Models/WorkflowDocument.cs` |
| A2 | ✅ | `LLMRequest.reasoning` | `Runtime/Interfaces.cs` |
| A3 | ✅ | Constrain `OnErrorCase.set_output` to `str \| None` (.NET treats it as a single expression string) | `Models/OnErrorDef.cs` |
| A4 | ✅ | Tighten `SwitchCaseDef.value` to `str \| None` (.NET stores raw scalar string) | `Models/StepDef.cs` |
| A5 | ✅ | Add `WorkflowCheckpoint` model (`run_id`, `workflow_name`, `workflow_yaml`, `next_step_index`, `step_outputs`, `inputs`, `status`, `timestamp`) | `Runtime/IWorkflowCheckpointer.cs` |

## B. Parser (YAML → model)

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| B6 | ✅ | Accept `dsl:` alias for `version:` | `WorkflowParser.cs` |
| B7 | ✅ | Raise on missing `version`/`dsl` (currently only fails when value ≠ 1) | `WorkflowParser.cs` |
| B8 | ✅ | Distinguish `required: bool` vs `required: list[str]` so a top-level boolean and an object-level `required` list of names co-exist | `WorkflowParser.cs::ParseInputDef` |
| B9 | ✅ | Mirror nested `OutputDef` "type-only" branch (no `expr`, has `type`) instead of falling through to backward-compat object output | `WorkflowParser.cs::ParseOutputDef` lines 219–253 |

## C. Compilation / validation

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| C10 | ✅ | Add `KnownInputTypes` / `KnownOutputTypes` schema validation, including recursive checks (`items` only on array, `properties` only on object, etc.) → emit `INVALID_INPUT_TYPE`, `INVALID_INPUT_SCHEMA` | `WorkflowValidator.cs` lines 312–474 |
| C11 | ✅ | Validate expressions inside nested output `properties` (currently only validates top-level `expr`) | `WorkflowValidator.cs` lines 458–466 |
| C12 | ✅ | Validate `cases[].when` expressions on `switch` | `WorkflowValidator.cs` lines 181–183 |
| C13 | ✅ | Add `INVALID_EXPORT` validation | `WorkflowValidator.cs` lines 42–49 |

## D. Expression engine (Jint replacement)

> **Strategy.** Python cannot embed Jint. Replace the current `eval()`-based
> evaluator with a small **AST-based JS-subset interpreter** (literals,
> identifiers, member access, computed indexing, unary/binary/logical/ternary,
> function call, optional chaining `?.`, nullish coalescing `??`). Keep zero
> third-party deps (no `py-mini-racer`, no `quickjs`). Reuse the same
> evaluator for `WFScript` (item F22).

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| D14 | ✅ | Replace `eval()`-based evaluator with hand-written tokenizer + Pratt parser AST | `Expressions/ExpressionEvaluator.cs` |
| D15 | ✅ | Expose top-level keys (`inputs`, `steps`, `env`, `error`, `step`) directly even when sharing names with built-in functions; verify after D14 | same |
| D16 | ✅ | Verify `formatDate` accepts ISO date AND unix-millis numeric input; either map .NET-format strings to `strftime`, or document Python-only formats | `BuiltInFunctions.cs` lines 166–176 |
| D17 | ✅ | Add execution timeout / statement / node limits (mirror Jint `MaxStatements`, `Timeout 5s`, memory bound) | `ExpressionEvaluator.cs` lines 35–41 |
| D18 | ✅ | Replace `[^}]+` regex in `StringInterpolator` by a brace-balanced scanner so `${...}` can contain `{}` | `StringInterpolator.cs` |

## E. Templating (Mustache subset)

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| E19 | ✅ | Add unit test for `{{!comment}}` consumption | `MustacheEngine.cs` |
| E20 | ✅ | Match .NET `WebUtility.HtmlEncode` exactly (single-quote / double-quote handling) | `MustacheEngine.cs` line 47 |
| E21 | ✅ | Numeric formatting must be invariant-culture (`123.45`); verify Python `str(float)` parity | `MustacheEngine.cs` line 143 |

## F. WFScript (`functions:` block)

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| F22 | ✅ | Replace regex-based `ScriptSandbox` with the AST evaluator from D14 so multi-statement bodies (declarations, `if/else`, locals, `return`) work | `Scripting/JintSandbox.cs` |
| F23 | ✅ | Pre-register built-in functions inside the WFScript scope so `lower(x)`, `len(x)` etc. are callable from user functions | `JintSandbox.cs` lines 127–143 |

## G. Runtime engine

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| G24 | ❌ | Add `resume_async(run_id, compiled_workflow)` mirroring `WorkflowEngine.ResumeAsync` | `WorkflowEngine.cs` lines 187–295 |
| G25 | ❌ | Add `IWorkflowCheckpointer` Protocol + `InMemoryWorkflowCheckpointer`; auto-save after each successful top-level step when `limits.run_id` is set | `Runtime/IWorkflowCheckpointer.cs`, `InMemoryWorkflowCheckpointer.cs` |
| G26 | ✅ | Wire structured logger (`logging.getLogger("gnougo_flow_core")`) for workflow start/end, step start/end | `WorkflowEngine.cs` |
| G27 | ✅ | Add OpenTelemetry-like content events (`gnougo-flow.step.input`, `gnougo-flow.step.output`) gated by `limits.log_step_content`. `gen_ai.content.prompt`/`completion` already wired in `llm.call` and `mcp.call` | `WorkflowEngine.cs` lines 391–424 |
| G28 | ✅ | Cancellation: `execute_async(..., ct: asyncio.Event \| None = None)` — checked between steps and during retry sleep, propagated through every `execute_steps_async` call site | `IWorkflowRuntime.cs` |

## H. Step executors

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| H29 | ✅ | `set` — deep-clones resolved input (parity with `JsonNode.DeepClone`) | `SetExecutor.cs` |
| H30 | ✅ | `emit` — event keys + level fallback match .NET; doc text aligned | `EmitExecutor.cs` |
| H31 | ✅ | `sequence` — propagates `ctx.ct` along with limits/depth/stack | `SequenceExecutor.cs` |
| H32 | ✅ | `parallel` — enforces `Limits.MaxParallelBranches` with count-aware `PARALLEL_LIMIT` message; per-branch data is `copy.deepcopy(ctx.data)` with isolated `steps` (parity with `JsonNode.DeepClone`) | `ParallelExecutor.cs` |
| H33 | ✅ | `loop.sequential` — supports `times`, `while`, `over` (Python-only extension) modes; honors `item_var`/`index_var`; mutual exclusion of `times`/`over` validated; `LOOP_LIMIT` message includes counts | `LoopSequentialExecutor.cs` |
| H34 | ✅ | `loop.parallel` — enforces both `MaxLoopIterations` (`LOOP_LIMIT`) and `MaxParallelBranches` (`PARALLEL_LIMIT`) with counts; deep-clones parent data; strips `__*__` keys | `LoopParallelExecutor.cs` |
| H35 | ✅ | `switch` — Form A (string compare via `JsonNode.ToJsonString`) + Form B (`when`); `MaxSwitchCases` error includes counts; `value: null` falls through to `when` | `SwitchExecutor.cs` |
| H36 | ✅ | `template.render` — routes through `ITemplateEngine` if set, else `MustacheEngine.render`; modes `text`/`json`/`markdown`/`html`; unknown mode → `INPUT_VALIDATION` | `TemplateRenderExecutor.cs` |
| H37 | ✅ | `llm.call` — read & forward `reasoning`; emit `gen_ai.request.reasoning_effort` | `LlmCallExecutor.cs` lines 105–115 |
| H38 | ✅ | `mcp.list` — server filtering/wildcard validation, capability listing, unsupported capability fallback, requested-only output arrays, and cache via `McpCacheHelper` | `McpListExecutor.cs` |
| H39 | ✅ | `mcp.call` — direct call, batch/auto-discover, cache-backed discovery, LLM-assisted selection/finalization prompt parity, prompt argument schemas, unsupported prompt fallback | `McpCallExecutor.cs` |
| H40 | ✅ | `human.input` — `IHumanInputProvider`, timeout including `timeout_ms: 0`, fields, choices, stable run id, waiting/received telemetry | `HumanInputExecutor.cs` |
| H41 | ✅ | `workflow.call` — `ref.kind ∈ {local, url}`; remote fetch via `IWorkflowFetcher` constrained by `FetchPolicy` (HTTPS scheme parsed via `urlparse`, `allowed_hostnames` allow-list, `require_integrity`, `max_size_bytes`); export selection (`export → entrypoint → single → first`); cycle/depth checks (`MaxCallDepth`); deep-copy of args/env | `WorkflowCallExecutor.cs` |
| H42 | ✅ | `workflow.plan` — defaults `reasoning="high"`, passes on main + prefilter calls | `WorkflowPlanExecutor.cs` |
| H43 | ✅ | `workflow.execute` — parse YAML returned by `workflow.plan`, compile, validate args/defaults, run under a sub-workflow telemetry span, propagate outputs/errors | `WorkflowExecuteExecutor.cs` |

## I. Interfaces / DTOs / integrations

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| I44 | ✅ | Mirror `LLMTool` / `LLMToolCall` field names (snake_case `input_schema`) | `Interfaces.cs` |
| I45 | ✅ | Reasoning normalization helpers (`normalize_openai_reasoning`, `normalize_ollama_think`) | `ChatRequestBuilder.cs` (in `GnOuGo.AI.Core`) |
| I46 | ✅ | Document `workflow.plan` default `"high"` (README) | — |
| I47 | ✅ | Add `IWorkflowCheckpointer` Protocol (`load_async`, `save_async`, `delete_async`, `list_async`) | `IWorkflowCheckpointer.cs` |
| I48 | ✅ | Port `JsonSchemaConverter` (`inputs_to_json_schema`, `outputs_to_json_schema`, …) → `gnougo_flow_core/json_schema.py` | `Models/JsonSchemaConverter.cs` |
| I49 | ✅ | Port `ConfiguredMcpClientFactory`, `InMemoryMcpClientFactory`, `RoutingLLMClientAdapter` → `gnougo_flow_core/integrations/` | corresponding `.cs` files |
| I50 | ✅ | Port `McpCacheHelper` (TTL cache for tools/prompts/resources per server) | `Runtime/McpCacheHelper.cs` |

## J. Error codes catalogue

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| J51 | ✅ | Verify `LLM_SCHEMA` and `JSON_PARSE` are emitted from the right places (`llm.call` structured-output validation, MCP structured finalization, template JSON parsing) | `ErrorCodes.cs` |

## K. CLI

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| K52 | ❌ | Restructure `cli.py` into subcommand group: `validate <yaml>`, `inspect <yaml>`, `run <yaml>` | parity with .NET CLI |
| K53 | ❌ | Add `-i key=value` (repeatable, scalar inputs) and `-j @path.json | -j '{...}'` JSON inputs to `run` | parity with .NET CLI |
| K54 | ❌ | Print structured outputs in the same JSON shape as the .NET CLI | parity |

## L. Test parity gaps

| # | Status | TODO | .NET source |
|---|:-:|---|---|
| L55 | ✅ | Port `McpCallLlmSelectionTests` → `tests/test_mcp_call_llm_selection.py` | `tests/GnOuGo.Flow.Tests/Runtime/McpCallLlmSelectionTests.cs` |
| L56 | ✅ | Port `WorkflowPlanMcpChatGuidanceTests` → `tests/test_workflow_plan_mcp_guidance.py` | `tests/.../WorkflowPlanMcpChatGuidanceTests.cs` |
| L57 | ✅ | Port `WorkflowExecuteExecutorTests` → `tests/test_workflow_execute.py` | `tests/.../WorkflowExecuteExecutorTests.cs` |
| L58 | ❌ | Port `WorkflowInputDefaultsTests` → `tests/test_workflow_input_defaults.py` | `tests/.../WorkflowInputDefaultsTests.cs` |
| L59 | ✅ | Port `ConfiguredMcpClientFactoryTests` → `tests/test_mcp_factory.py` | `tests/.../ConfiguredMcpClientFactoryTests.cs` |
| L60 | ✅ | Add `tests/test_json_schema.py` covering `JsonSchemaConverter` parity | `tests/.../JsonSchemaConverterTests.cs` |
| L61 | ❌ | Add `tests/test_resume.py` covering checkpoint save+resume | new |
| L62 | ✅ | `tests/test_reasoning_field.py` covering reasoning round-trip + `workflow.plan` default `"high"` | new |

---

## Recommended phasing (re-ordered to ship in slices)

1. **Phase 1 — Models, parser, compiler basics**: A3, A4, A5, B7, B8, B9, C10, C11, C12, C13, I47, I48 (+ tests L60)
2. **Phase 2 — Expression engine, templating, WFScript**: D14, D15, D16, D17, D18, E19, E20, E21, F22, F23
3. **Phase 3 — Runtime polish & simple executors**: G26, G27, G28, H29–H31, H35, H36
4. **Phase 4 — Loops, parallel, workflow.call**: H32, H33, H34, H41 (+ new tests)
5. **Phase 5 — LLM/MCP/human executors + integrations**: H38, H39, H40, I49, I50, L55, L59
6. **Phase 6 — workflow.plan polish + workflow.execute**: H43, J51, L56, L57
7. **Phase 7 — CLI + checkpointer**: G24, G25, K52, K53, K54, L58, L61

---

## How to keep this in sync with the .NET reference

- When adding a new behaviour to `GnOuGo.Flow.Core`, add a TODO row in this
  file (or check ✅ if you also port it).
- When closing an item, replace ❌/⚠️ by ✅ and link to the test that
  guarantees parity.
- The agent prompt for "scan everything and port what's missing" should always
  start by re-reading this file before re-running a full diff.

