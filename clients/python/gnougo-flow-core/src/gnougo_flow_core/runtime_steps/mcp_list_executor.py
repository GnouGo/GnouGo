from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403

class McpListExecutor:
    step_type = "mcp.list"
    step_description = "Discover MCP tools/resources/prompts for selected servers."
    dsl_snippet = """
### mcp.list - Discover MCP capabilities
```yaml
- id: discover_mcp
  type: mcp.list
  input:
    servers: ["*"]
    include: [tools, prompts, resources]
```
Output: `{ status, servers, tools, resources, prompts, text }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "mcp.list input is malformed."),
        (ErrorCodes.MCP_CONNECTION_ERROR, False, "no MCP client factory configured."),
        (ErrorCodes.MCP_TIMEOUT, True, "mcp.list timed out."),
        (ErrorCodes.MCP_LIST_ERROR, False, "mcp.list failed."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        factory = ctx.engine.mcp_client_factory
        if factory is None:
            raise WorkflowRuntimeException(ErrorCodes.MCP_CONNECTION_ERROR, "No IMcpClientFactory configured")

        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.list input must be object")

        servers = input_obj.get("servers")
        if not isinstance(servers, list) or not servers:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.list requires 'servers' as a non-empty array")

        include = input_obj.get("include") if isinstance(input_obj.get("include"), list) else ["tools"]
        include_set = {str(v).lower() for v in include}
        valid_include = {"tools", "resources", "prompts"}
        invalid = include_set - valid_include
        if invalid:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"mcp.list invalid include value(s): {sorted(invalid)}")

        ctx.set_telemetry_attribute("gen_ai.operation.name", "tool_list")
        ctx.set_telemetry_attribute("mcp.include", ",".join(sorted(include_set)))

        if servers == ["*"]:
            server_names = [m.name for m in factory.server_metadata]
        else:
            server_names = [str(s) for s in servers]

        if not server_names:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.list found no configured MCP servers")

        ctx.set_telemetry_attribute("mcp.server.count", len(server_names))
        if len(server_names) == 1:
            ctx.set_telemetry_attribute("mcp.server.name", server_names[0])
        else:
            ctx.set_telemetry_attribute("mcp.server.names", ",".join(server_names))

        timeout_ms = input_obj.get("timeout_ms")
        timeout = float(timeout_ms) / 1000.0 if isinstance(timeout_ms, (int, float)) else None

        result = {"status": "ok", "servers": [], "tools": [], "resources": [], "prompts": []}
        errors = 0

        for server_name in server_names:
            try:
                if timeout is not None:
                    session = await asyncio.wait_for(factory.get_client_async(server_name), timeout=timeout)
                else:
                    session = await factory.get_client_async(server_name)
                server_entry = {"name": server_name, "status": "ok"}
                if "tools" in include_set:
                    tools = await (asyncio.wait_for(session.list_tools_async(), timeout=timeout) if timeout is not None else session.list_tools_async())
                    server_entry["tools"] = [t.model_dump() for t in tools]
                    for t in tools:
                        result["tools"].append({"server": server_name, **t.model_dump()})
                if "resources" in include_set:
                    try:
                        resources = await (asyncio.wait_for(session.list_resources_async(), timeout=timeout) if timeout is not None else session.list_resources_async())
                    except Exception as ex:
                        resources = []
                        ctx.set_telemetry_attribute("mcp.resources_unsupported", True)
                        ctx.add_telemetry_event(
                            "mcp.capability.unsupported",
                            [
                                ("mcp.server.name", server_name),
                                ("mcp.method", "resources/list"),
                                ("mcp.reason", str(ex)),
                            ],
                        )
                    server_entry["resources"] = [r.model_dump() for r in resources]
                    for r in resources:
                        result["resources"].append({"server": server_name, **r.model_dump()})
                if "prompts" in include_set:
                    try:
                        prompts = await (asyncio.wait_for(session.list_prompts_async(), timeout=timeout) if timeout is not None else session.list_prompts_async())
                    except Exception as ex:
                        prompts = []
                        ctx.set_telemetry_attribute("mcp.prompts_unsupported", True)
                        ctx.add_telemetry_event(
                            "mcp.capability.unsupported",
                            [
                                ("mcp.server.name", server_name),
                                ("mcp.method", "prompts/list"),
                                ("mcp.reason", str(ex)),
                            ],
                        )
                    server_entry["prompts"] = [p.model_dump() for p in prompts]
                    for p in prompts:
                        result["prompts"].append({"server": server_name, **p.model_dump()})
                result["servers"].append(server_entry)
            except TimeoutError as exc:
                raise WorkflowRuntimeException(ErrorCodes.MCP_TIMEOUT, f"mcp.list timed out: {exc}", retryable=True) from exc
            except Exception as exc:
                errors += 1
                result["servers"].append({"name": server_name, "status": "error", "error": str(exc)})

        if errors and errors == len(server_names):
            result["status"] = "error"
        elif errors:
            result["status"] = "partial"

        lines: list[str] = [f"MCP Servers ({len(server_names)})"]
        for server in result["servers"]:
            lines.append("")
            lines.append(f"## Server: {server.get('name', '(unknown)')}")
            status = server.get("status", "ok")
            lines.append(f"Status: {status}")
            if server.get("error"):
                lines.append(f"Error: {server['error']}")
            if isinstance(server.get("tools"), list):
                lines.append(f"### Tools ({len(server['tools'])})")
                for t in server["tools"]:
                    lines.append(f"- {t.get('name')}: {t.get('description') or '(no description)'}")
            if isinstance(server.get("resources"), list):
                lines.append(f"### Resources ({len(server['resources'])})")
                for r in server["resources"]:
                    lines.append(f"- {r.get('name')} ({r.get('uri')}): {r.get('description') or '(no description)'}")
            if isinstance(server.get("prompts"), list):
                lines.append(f"### Prompts ({len(server['prompts'])})")
                for p in server["prompts"]:
                    lines.append(f"- {p.get('name')}: {p.get('description') or '(no description)'}")
        result["text"] = "\n".join(lines).strip()

        ctx.set_telemetry_attribute("mcp.tools_count", len(result["tools"]))
        ctx.set_telemetry_attribute("mcp.resources_count", len(result["resources"]))
        ctx.set_telemetry_attribute("mcp.prompts_count", len(result["prompts"]))
        ctx.set_telemetry_attribute("mcp.failed_servers_count", errors)
        ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "stop")

        return result
