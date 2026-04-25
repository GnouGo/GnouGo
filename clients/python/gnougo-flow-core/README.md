# gnougo-flow-core

Python 3.10+ port of `GnOuGo.Flow.Core` (.NET).

The .NET library at `src/GnOuGo.Flow.Core/` is the **source of truth**.
This Python package mirrors its public surface as closely as a Python idiom
allows. See [`PORTING_TODO.md`](PORTING_TODO.md) for the full gap analysis and
remaining work items.

## Features (parity status)

| Area                          | Status |
|------------------------------|--------|
| YAML DSL parser (`version:` / `dsl:` alias) | ✅ |
| Validation + compilation pipeline | ✅ (subset — see PORTING_TODO §C) |
| Expression interpolation `${...}` + built-in functions | ✅ (AST-based JS-subset interpreter) |
| Mustache `template.render` engine | ✅ |
| WFScript (`functions:` block) | ✅ multi-statement (var/let/const, if/else, return) |
| Runtime engine + step registry | ✅ |
| Step types: `set`, `emit`, `sequence`, `parallel`, `loop.sequential`, `loop.parallel`, `switch`, `template.render`, `llm.call`, `mcp.list`, `mcp.call`, `human.input`, `workflow.call`, `workflow.plan`, `workflow.execute` | ✅ |
| MCP integrations (`InMemoryMcpClientFactory`, `ConfiguredMcpClientFactory`, cache helper) | ✅ |
| `LLMRequest.reasoning` field | ✅ |
| `workflow.plan` defaults `reasoning="high"` | ✅ |
| `JsonSchemaConverter` (inputs/outputs → JSON Schema) | ✅ |
| `WorkflowCheckpointer` + `ResumeAsync` | ❌ TODO |
| CLI: `validate` / `inspect` / `run` subcommands | ❌ TODO (current CLI is `run`-only) |

## Project layout

- `src/gnougo_flow_core/` library code
- `tests/` unit tests
- `PORTING_TODO.md` gap analysis vs the .NET reference

## Install with uv

```bash
uv sync --extra dev
```

## Run tests

```bash
uv run --extra dev pytest
```

## Quick run

```bash
uv run gnougo-flow path/to/workflow.yaml --workflow main --inputs '{"name":"World"}'
```

## Reasoning / thinking effort

`LLMRequest.reasoning` controls the *thinking* effort on capable models
(OpenAI o-series, gpt-5, Anthropic via Copilot, Ollama deepseek/qwen, …).
Accepted values: `"minimal" | "low" | "medium" | "high" | "max" | "auto" | None`.

- `llm.call` reads `input.reasoning` and forwards it to the request.
  Default = omitted (provider decides).
- `workflow.plan` defaults `generator.reasoning` to `"high"` (max) because
  planning is heavy reasoning work. Overridable per call.
- Provider-specific normalization is exposed via
  `gnougo_flow_core.reasoning.normalize_openai_reasoning` /
  `normalize_ollama_think` (mirrors `ChatRequestBuilder` in
  `GnOuGo.AI.Core`).

## Notes

- The public API mirrors the C# core concepts (`WorkflowParser`,
  `WorkflowCompiler`, `WorkflowEngine`, executors, runtime interfaces).
- Runtime integrations (`ILLMClient`, `IMcpClientFactory`, human input
  provider, workflow fetcher) are protocol-based and injected by the caller.
- Phase 5 MCP helpers live in `gnougo_flow_core.integrations`:
  `InMemoryMcpClientFactory` / `MockMcpServerConfig` for tests and local
  demos, `ConfiguredMcpClientFactory` / `McpSessionAdapter` for injected MCP
  clients, and `RoutingLLMClientAdapter` for adapting a routing LLM client.
- `WorkflowEngine.mcp_cache` defaults to `McpCacheHelper`, a 5-minute sliding
  TTL cache for MCP tools/resources/prompts per server. Set it to `None` to
  disable capability caching.
- Expression engine: see `gnougo_flow_core/_jsmini.py` for the in-tree
  JS-subset interpreter that backs both `${...}` expressions and the
  WFScript `functions:` block (no third-party dependency, AOT-friendly).
  Execution limits (max statements, max nodes, 5s wall-clock timeout, max
  call depth) mirror the .NET `Jint` configuration.

