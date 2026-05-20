from __future__ import annotations

from gnougo_flow_core.mcp_cache import (
    cache_prompts,
    cache_resources,
    cache_tools,
    get_cached_prompts,
    get_cached_resources,
    get_cached_tools,
)
from gnougo_flow_core.runtime import *  # noqa: F401,F403

_VALID_INCLUDES = {"tools", "resources", "prompts"}


def _is_unsupported_capability(exc: Exception, method_name: str) -> bool:
    current: BaseException | None = exc
    while current is not None:
        msg = str(current).lower()
        if method_name.lower() in msg and any(
            phrase in msg
            for phrase in ("not available", "not implemented", "method not found", "no handler")
        ):
            return True
        current = current.__cause__ or current.__context__
    return False


def _dump_model(value: Any) -> dict[str, Any]:
    return value.model_dump() if hasattr(value, "model_dump") else dict(value)


def _get_property(value: Any, name: str) -> Any:
    if isinstance(value, dict):
        return value.get(name)
    return getattr(value, name, None)


def _resolve_server_names(input_obj: dict[str, Any], factory: Any) -> list[str]:
    servers = input_obj.get("servers")
    if not isinstance(servers, list) or not servers:
        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.list requires 'servers' as a non-empty array of strings")

    requested: list[str] = []
    contains_wildcard = False
    for item in servers:
        name = "" if item is None else str(item)
        if not name.strip():
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.list 'servers' contains an empty or null entry")
        if name == "*":
            contains_wildcard = True
        requested.append(name)

    if contains_wildcard:
        if len(requested) > 1:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.list 'servers' cannot mix '*' with explicit server names")
        names: list[str] = []
        seen: set[str] = set()
        for meta in getattr(factory, "server_metadata", []) or []:
            name = getattr(meta, "name", None) if not isinstance(meta, dict) else meta.get("name")
            if isinstance(name, str) and name.strip() and name not in seen:
                seen.add(name)
                names.append(name)
        if not names:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "mcp.list 'servers: [\"*\"]' found no configured MCP servers")
        return names

    deduped: list[str] = []
    seen: set[str] = set()
    for name in requested:
        if name not in seen:
            seen.add(name)
            deduped.append(name)
    return deduped


def _parse_includes(input_obj: dict[str, Any]) -> set[str]:
    include_node = input_obj.get("include")
    includes: set[str] = set()
    if isinstance(include_node, list):
        for item in include_node:
            val = "" if item is None else str(item)
            if not val.strip():
                continue
            lowered = val.lower()
            if lowered not in _VALID_INCLUDES:
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"mcp.list invalid include value '{val}'. Valid: tools, resources, prompts")
            includes.add(lowered)
    if not includes:
        includes.add("tools")
    return includes


async def _maybe_timeout(coro: Any, timeout: float | None) -> Any:
    return await (asyncio.wait_for(coro, timeout=timeout) if timeout is not None else coro)


def _resolve_effective_timeout_ms(input_obj: dict[str, Any], server_names: list[str], factory: Any) -> int | None:
    requested = input_obj.get("timeout_ms")
    requested_ms = int(requested) if isinstance(requested, (int, float)) else None
    configured_values = [_resolve_configured_discovery_timeout_ms(factory, name) for name in server_names]
    configured_ms = max((value for value in configured_values if value is not None), default=None)
    if requested_ms is not None and configured_ms is not None:
        return max(requested_ms, configured_ms)
    return requested_ms if requested_ms is not None else configured_ms


def _resolve_configured_discovery_timeout_ms(factory: Any, server_name: str) -> int | None:
    for metadata in getattr(factory, "server_metadata", []) or []:
        name = _get_property(metadata, "name")
        if not isinstance(name, str) or name.lower() != server_name.lower():
            continue
        seconds = _get_property(metadata, "discovery_timeout_seconds")
        if seconds is None:
            seconds = _get_property(metadata, "DiscoveryTimeoutSeconds")
        try:
            seconds_int = int(seconds)
        except (TypeError, ValueError):
            return None
        return seconds_int * 1000 if seconds_int > 0 else None
    return None


async def _try_list_resources(session: IMcpSession, server_name: str, ctx: StepExecutionContext, timeout: float | None) -> list[McpResourceInfo]:
    try:
        return await _maybe_timeout(session.list_resources_async(), timeout)
    except Exception as exc:
        if not _is_unsupported_capability(exc, "resources/list"):
            raise
        ctx.set_telemetry_attribute("mcp.resources_unsupported", True)
        ctx.add_telemetry_event(
            "mcp.capability.unsupported",
            [("mcp.server.name", server_name), ("mcp.method", "resources/list"), ("mcp.reason", str(exc))],
        )
        return []


async def _try_list_prompts(session: IMcpSession, server_name: str, ctx: StepExecutionContext, timeout: float | None) -> list[McpPromptInfo]:
    try:
        return await _maybe_timeout(session.list_prompts_async(), timeout)
    except Exception as exc:
        if not _is_unsupported_capability(exc, "prompts/list"):
            raise
        ctx.engine.logger.warning("mcp.list: prompts/list not supported on '%s': %s", server_name, exc)
        ctx.set_telemetry_attribute("mcp.prompts_unsupported", True)
        ctx.add_telemetry_event(
            "mcp.capability.unsupported",
            [("mcp.server.name", server_name), ("mcp.method", "prompts/list"), ("mcp.reason", str(exc))],
        )
        return []


async def _fetch_server_capabilities(
    factory: Any,
    server_name: str,
    includes: set[str],
    ctx: StepExecutionContext,
    timeout: float | None,
) -> dict[str, Any]:
    cache = ctx.engine.mcp_cache
    want_tools = "tools" in includes
    want_resources = "resources" in includes
    want_prompts = "prompts" in includes

    tools = get_cached_tools(cache, server_name) if want_tools else None
    resources = get_cached_resources(cache, server_name) if want_resources else None
    prompts = get_cached_prompts(cache, server_name) if want_prompts else None

    if (
        (not want_tools or tools is not None)
        and (not want_resources or resources is not None)
        and (not want_prompts or prompts is not None)
    ):
        ctx.engine.logger.debug("mcp.list: serving '%s' entirely from cache", server_name)
        return {"name": server_name, "status": "ok", "tools": tools, "resources": resources, "prompts": prompts}

    session = await _maybe_timeout(factory.get_client_async(server_name), timeout)
    if want_tools and tools is None:
        tools = await _maybe_timeout(session.list_tools_async(), timeout)
        cache_tools(cache, server_name, tools)
    if want_resources and resources is None:
        resources = await _try_list_resources(session, server_name, ctx, timeout)
        cache_resources(cache, server_name, resources)
    if want_prompts and prompts is None:
        prompts = await _try_list_prompts(session, server_name, ctx, timeout)
        cache_prompts(cache, server_name, prompts)
    return {"name": server_name, "status": "ok", "tools": tools, "resources": resources, "prompts": prompts}


def _build_aggregate_result(server_results: list[dict[str, Any]], includes: set[str], ctx: StepExecutionContext) -> dict[str, Any]:
    success_count = sum(1 for r in server_results if str(r.get("status", "ok")).lower() == "ok")
    error_count = len(server_results) - success_count
    result: dict[str, Any] = {
        "status": "ok" if error_count == 0 else ("error" if success_count == 0 else "partial"),
        "servers": [],
    }
    if "tools" in includes:
        result["tools"] = []
    if "resources" in includes:
        result["resources"] = []
    if "prompts" in includes:
        result["prompts"] = []

    total_tools = total_resources = total_prompts = 0
    lines: list[str] = [f"MCP Servers ({len(server_results)})"]
    for server in server_results:
        name = server.get("name", "(unknown)")
        server_entry: dict[str, Any] = {"name": name, "status": server.get("status", "ok")}
        lines.append("")
        lines.append(f"## Server: {name}")
        if server_entry["status"] != "ok":
            if server.get("error"):
                server_entry["error"] = str(server["error"])
                lines.append("Status: error")
                lines.append(f"Error: {server['error']}")
            result["servers"].append(server_entry)
            continue

        if "tools" in includes:
            tools = list(server.get("tools") or [])
            total_tools += len(tools)
            server_tools = []
            lines.append("")
            lines.append(f"### Tools ({len(tools)})")
            for tool in tools:
                item = {"server": name, **_dump_model(tool)}
                server_tools.append(dict(item))
                result["tools"].append(item)
                lines.append(f"- **{item.get('name')}**: {item.get('description') or '(no description)'}")
            server_entry["tools"] = server_tools

        if "resources" in includes:
            resources = list(server.get("resources") or [])
            total_resources += len(resources)
            server_resources = []
            lines.append("")
            lines.append(f"### Resources ({len(resources)})")
            for resource in resources:
                item = {"server": name, **_dump_model(resource)}
                server_resources.append(dict(item))
                result["resources"].append(item)
                lines.append(
                    f"- **{item.get('name')}** ({item.get('uri')}): "
                    f"{item.get('description') or '(no description)'}"
                )
            server_entry["resources"] = server_resources

        if "prompts" in includes:
            prompts = list(server.get("prompts") or [])
            total_prompts += len(prompts)
            server_prompts = []
            lines.append("")
            lines.append(f"### Prompts ({len(prompts)})")
            for prompt in prompts:
                item = {"server": name, **_dump_model(prompt)}
                server_prompts.append(dict(item))
                result["prompts"].append(item)
                args = item.get("arguments") or []
                args_text = f" (args: {', '.join(str(a.get('name')) for a in args if isinstance(a, dict))})" if args else ""
                lines.append(
                    f"- **{item.get('name')}**{args_text}: "
                    f"{item.get('description') or '(no description)'}"
                )
            server_entry["prompts"] = server_prompts

        result["servers"].append(server_entry)

    result["text"] = "\n".join(lines).rstrip()
    ctx.set_telemetry_attribute("mcp.tools_count", total_tools)
    ctx.set_telemetry_attribute("mcp.resources_count", total_resources)
    ctx.set_telemetry_attribute("mcp.prompts_count", total_prompts)
    ctx.set_telemetry_attribute("mcp.failed_servers_count", error_count)
    ctx.set_telemetry_attribute("gen_ai.response.finish_reason", "stop")
    return result

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
The discovered `tools` and `prompts` arrays can be passed directly into `mcp.call.input.tools` and/or `mcp.call.input.prompts`:
```yaml
- id: smart_call
  type: mcp.call
  input:
    server: GnOuGo.Browser.Mcp
    model: gpt-4o-mini
    prompt: "Choose the right MCP capability and call it"
    tools: "${data.steps.discover_mcp.tools}"
    prompts: "${data.steps.discover_mcp.prompts}"
```
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

        server_names = _resolve_server_names(input_obj, factory)
        include_set = _parse_includes(input_obj)

        ctx.set_telemetry_attribute("gen_ai.operation.name", "tool_list")
        ctx.set_telemetry_attribute("mcp.include", ",".join(sorted(include_set)))
        ctx.set_telemetry_attribute("mcp.server.count", len(server_names))
        if len(server_names) == 1:
            ctx.set_telemetry_attribute("mcp.server.name", server_names[0])
        else:
            ctx.set_telemetry_attribute("mcp.server.names", ",".join(server_names))

        timeout_ms = _resolve_effective_timeout_ms(input_obj, server_names, factory)
        timeout = float(timeout_ms) / 1000.0 if timeout_ms is not None else None
        if timeout_ms is not None:
            ctx.set_telemetry_attribute("mcp.timeout_ms", timeout_ms)
        try:
            if len(server_names) == 1:
                server_result = await _fetch_server_capabilities(factory, server_names[0], include_set, ctx, timeout)
                return _build_aggregate_result([server_result], include_set, ctx)

            server_results: list[dict[str, Any]] = []
            for server_name in server_names:
                try:
                    server_results.append(await _fetch_server_capabilities(factory, server_name, include_set, ctx, timeout))
                except Exception as exc:
                    ctx.engine.logger.warning("mcp.list: failed to list capabilities for '%s': %s", server_name, exc)
                    server_results.append({"name": server_name, "status": "error", "error": str(exc)})
            return _build_aggregate_result(server_results, include_set, ctx)
        except WorkflowRuntimeException:
            raise
        except TimeoutError as exc:
            raise WorkflowRuntimeException(ErrorCodes.MCP_TIMEOUT, f"mcp.list timed out after {timeout_ms}ms", retryable=True) from exc
        except Exception as exc:
            raise WorkflowRuntimeException(ErrorCodes.MCP_LIST_ERROR, f"mcp.list failed: {exc}", retryable=False) from exc
