# gnougo-flow-cli

Python CLI for validating, inspecting, and executing saved GnOuGo workflows.
Workflow planning is provided by the .NET Flow CLI, Flow Server, and Agent.Server
using [Planner v2](../../../docs/workflow-planning-v2.md).

## Install, test, and build

```sh
uv sync --locked --extra dev
uv run --locked --extra dev ruff check .
uv run --locked --extra dev pytest -q
uv build
```

Python 3.10+ remains supported. The MCP 2 adapter reads the SDK's snake_case
model attributes and preserves camelCase wire aliases in returned content blocks.
The stdio integration test covers discovery, tool success/error results, prompts,
resources, and reconnecting to a real local SDK server.

## Usage

```sh
uv run gnougo-flow-cli validate examples/basic.yaml
uv run gnougo-flow-cli inspect examples/basic.yaml
uv run gnougo-flow-cli run examples/basic.yaml -i name=World
uv run gnougo-flow-cli run examples/basic.yaml -j '{"name":"World"}'
uv run gnougo-flow-cli run examples/basic.yaml -j @inputs.json
uv run gnougo-flow-cli examples list
```

Use `--llm openai` for a configured provider or `--llm stub` for local fixtures.
Use `--mcp real` for configured MCP servers or `--mcp stub` for the demo server.
The `auto` choices use the configured service when available. These are runtime
transport choices; the Python CLI contains no workflow planner.

## Configuration

Settings come from the specified settings file, or conventional local example files when no file is selected,
`.env`, and environment variables prefixed with `GNOUGO__`. Configure credentials
through the environment, such as `GNOUGO__OPENAI__API_KEY`.

MCP servers use `LLM.McpServers` with declared transport, command or URL, arguments,
and working directory. Relative `--project` paths resolve against the repository
workspace. Discovery cache expiration defaults to one hour and can be configured
with `McpCapabilityCache.SlidingExpirationSeconds`.

Telemetry is disabled by default. Use `--otlp-endpoint` or the `telemetry` settings
to export OTLP HTTP traces. Workflow source telemetry is truncated at 64 KiB.
The local `run` command executes authored YAML without persistence. Its in-memory
checkpoint option was removed. Durable runs and agent stages belong to schema-9 .NET
hosts. Control an existing run with the shared tenant-scoped API:

```bash
gnougo-flow-cli runs --server http://localhost:5000 --tenant default
gnougo-flow-cli runs --server http://localhost:5000 --tenant default --id RUN_ID
gnougo-flow-cli runs --server http://localhost:5000 --tenant default --id RUN_ID --command resume --revision 7
```

`cancel` also requires `--revision`; `reconcile` additionally requires `--invocation`.
After a transport failure, inspect the current run before retrying. Commands do not
retry automatically. Old approvals require regeneration and approval with schema 9.
