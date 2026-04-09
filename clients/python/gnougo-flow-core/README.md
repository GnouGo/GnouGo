# gnougo-flow-core

Python 3.10+ port of `GnOuGo.Flow.Core`.

## Features

- YAML workflow DSL parser (`dsl: 1`)
- Validation + compilation pipeline
- Expression interpolation with built-in functions
- Mustache rendering engine (`template.render`)
- Runtime engine with step registry and executors
- Core step types: `set`, `sequence`, `parallel`, `loop.sequential`, `loop.parallel`, `switch`, `template.render`, `llm.call`, `workflow.call`, `workflow.execute`, `workflow.plan`, `emit`, `human.input`, `mcp.list`, `mcp.call`
- Unit tests

## Project layout

- `src/gnougo_flow_core/` library code
- `tests/` unit tests

## Install with uv

```bash
uv sync
```

## Run tests

```bash
uv run pytest
```

## Quick run

```bash
uv run gnougo-flow path/to/workflow.yaml --workflow main --inputs '{"name":"World"}'
```

## Notes

- The public API mirrors the C# core concepts (`WorkflowParser`, `WorkflowCompiler`, `WorkflowEngine`, executors, runtime interfaces).
- Runtime integrations (`ILLMClient`, `IMcpClientFactory`, human input provider, workflow fetcher) are protocol-based and injected by the caller.

