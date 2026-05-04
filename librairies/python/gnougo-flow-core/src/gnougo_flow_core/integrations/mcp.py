from __future__ import annotations

import asyncio
import inspect
import json
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable

from ..errors import ErrorCodes, WorkflowRuntimeException
from ..models import (
    McpCallResult,
    McpGetPromptResult,
    McpPromptInfo,
    McpPromptMessage,
    McpResourceInfo,
    McpServerMetadata,
    McpToolInfo,
)

ToolHandler = Callable[[Any], McpCallResult | Awaitable[McpCallResult]]
PromptHandler = Callable[[Any], McpGetPromptResult | Awaitable[McpGetPromptResult]]


@dataclass(slots=True)
class MockMcpServerConfig:
    description: str | None = None
    tools: list[McpToolInfo] = field(default_factory=list)
    resources: list[McpResourceInfo] = field(default_factory=list)
    prompts: list[McpPromptInfo] = field(default_factory=list)
    tool_handlers: dict[str, ToolHandler] = field(default_factory=dict)
    prompt_handlers: dict[str, PromptHandler] = field(default_factory=dict)


class InMemoryMcpClientFactory:
    def __init__(self) -> None:
        self._servers: dict[str, MockMcpServerConfig] = {}

    @property
    def server_metadata(self) -> list[McpServerMetadata]:
        return [
            McpServerMetadata(name=name, description=config.description)
            for name, config in self._servers.items()
        ]

    def register_server(self, name: str, config: MockMcpServerConfig) -> None:
        self._servers[name] = config

    async def get_client_async(self, server_name: str) -> "InMemoryMcpSession":
        if server_name not in self._servers:
            available = ", ".join(self._servers.keys())
            raise WorkflowRuntimeException(
                ErrorCodes.MCP_SERVER_NOT_FOUND,
                f"MCP server '{server_name}' not found. Available: [{available}]",
            )
        await asyncio.sleep(0)
        return InMemoryMcpSession(server_name, self._servers[server_name])


class InMemoryMcpSession:
    def __init__(self, server_name: str, config: MockMcpServerConfig) -> None:
        self.server_name = server_name
        self._config = config

    async def list_tools_async(self) -> list[McpToolInfo]:
        await asyncio.sleep(0)
        return list(self._config.tools)

    async def list_resources_async(self) -> list[McpResourceInfo]:
        await asyncio.sleep(0)
        return list(self._config.resources)

    async def list_prompts_async(self) -> list[McpPromptInfo]:
        await asyncio.sleep(0)
        return list(self._config.prompts)

    async def call_tool_async(self, tool_name: str, arguments: Any, mcp_meta: dict[str, Any] | None = None) -> McpCallResult:
        handler = self._config.tool_handlers.get(tool_name)
        if handler is not None:
            result = handler(arguments)
            if inspect.isawaitable(result):
                result = await result
            return result
        await asyncio.sleep(0)
        return McpCallResult(
            is_error=False,
            content={
                "mock": True,
                "tool": tool_name,
                "message": f"[Mock MCP] Tool '{tool_name}' called on server '{self.server_name}'",
            },
            model="mock-model",
            usage={"prompt_tokens": 5, "completion_tokens": 15, "total_tokens": 20},
        )

    async def get_prompt_async(self, prompt_name: str, arguments: Any) -> McpGetPromptResult:
        handler = self._config.prompt_handlers.get(prompt_name)
        if handler is not None:
            result = handler(arguments)
            if inspect.isawaitable(result):
                result = await result
            return result
        await asyncio.sleep(0)
        args_repr = json.dumps(arguments, ensure_ascii=False) if arguments is not None else "null"
        return McpGetPromptResult(
            description=f"[Mock MCP] Prompt '{prompt_name}' on server '{self.server_name}'",
            messages=[
                McpPromptMessage(
                    role="user",
                    content=f"[Mock prompt '{prompt_name}' with args: {args_repr}]",
                )
            ],
            model="mock-model",
            usage={"prompt_tokens": 8, "completion_tokens": 12, "total_tokens": 20},
        )


@dataclass(slots=True)
class McpServerOptions:
    type: str = "http"
    url: str = ""
    command: str | None = None
    args: list[str] | None = None
    api_key: str | None = None
    description: str | None = None
    client: Any = None


class ConfiguredMcpClientFactory:
    """Configured MCP factory.

    The Python port keeps the same testable surface as the .NET implementation.
    Real transports can be injected through `McpServerOptions.client`; otherwise
    attempting to open a transport raises a clear integration error rather than
    adding a mandatory third-party dependency to the core package.
    """

    def __init__(self, server_configs: dict[str, McpServerOptions | dict[str, Any]]) -> None:
        self._server_configs = {
            name: (cfg if isinstance(cfg, McpServerOptions) else McpServerOptions(**cfg))
            for name, cfg in server_configs.items()
        }
        self._clients: dict[str, Any] = {}

    @property
    def server_metadata(self) -> list[McpServerMetadata]:
        return [
            McpServerMetadata(name=name, description=config.description)
            for name, config in self._server_configs.items()
        ]

    async def get_client_async(self, server_name: str) -> "McpSessionAdapter":
        if server_name not in self._server_configs:
            available = ", ".join(self._server_configs.keys())
            raise WorkflowRuntimeException(
                ErrorCodes.MCP_SERVER_NOT_FOUND,
                f"MCP server '{server_name}' not found. Available: [{available}]",
            )

        if server_name not in self._clients:
            config = self._server_configs[server_name]
            if config.client is None:
                raise WorkflowRuntimeException(
                    ErrorCodes.MCP_CONNECTION_ERROR,
                    (
                        f"MCP server '{server_name}' has no injected client. "
                        "Install/configure a transport adapter before use."
                    ),
                )
            self._clients[server_name] = config.client
        await asyncio.sleep(0)
        return McpSessionAdapter(server_name, self._clients[server_name])

    async def dispose_async(self) -> None:
        for client in self._clients.values():
            close = getattr(client, "dispose_async", None) or getattr(client, "aclose", None) or getattr(client, "close", None)
            if close is None:
                continue
            value = close()
            if inspect.isawaitable(value):
                await value
        self._clients.clear()

    @staticmethod
    def is_unexpected_server_exit(exc: BaseException) -> bool:
        return is_unexpected_server_exit(exc)


class McpSessionAdapter:
    def __init__(self, server_name: str, client: Any) -> None:
        self.server_name = server_name
        self._client = client

    async def list_tools_async(self) -> list[McpToolInfo]:
        tools = await _maybe_await(_call_first(self._client, ["list_tools_async", "list_tools"]))
        return [_coerce_tool(t) for t in (tools or [])]

    async def list_resources_async(self) -> list[McpResourceInfo]:
        capabilities = getattr(self._client, "server_capabilities", None) or getattr(self._client, "capabilities", None)
        if capabilities is not None and getattr(capabilities, "resources", object()) is None:
            return []
        resources = await _maybe_await(_call_first(self._client, ["list_resources_async", "list_resources"]))
        return [_coerce_resource(r) for r in (resources or [])]

    async def list_prompts_async(self) -> list[McpPromptInfo]:
        capabilities = getattr(self._client, "server_capabilities", None) or getattr(self._client, "capabilities", None)
        if capabilities is not None and getattr(capabilities, "prompts", object()) is None:
            return []
        prompts = await _maybe_await(_call_first(self._client, ["list_prompts_async", "list_prompts"]))
        return [_coerce_prompt(p) for p in (prompts or [])]

    async def call_tool_async(self, tool_name: str, arguments: Any, mcp_meta: dict[str, Any] | None = None) -> McpCallResult:
        result = await _maybe_await(_call_tool_on_client(self._client, tool_name, convert_arguments(arguments), mcp_meta))
        if isinstance(result, McpCallResult):
            return result
        content = _build_content(result)
        return McpCallResult(is_error=bool(_get(result, "is_error", False)), content=content)

    async def get_prompt_async(self, prompt_name: str, arguments: Any) -> McpGetPromptResult:
        result = await _maybe_await(_call_first(self._client, ["get_prompt_async", "get_prompt"], prompt_name, convert_arguments(arguments)))
        if isinstance(result, McpGetPromptResult):
            return result
        messages = []
        for msg in _get(result, "messages", []) or []:
            content = _get(msg, "content", "")
            if not isinstance(content, str):
                content = _get(content, "text", str(content))
            messages.append(McpPromptMessage(role=str(_get(msg, "role", "user")).lower(), content=content))
        return McpGetPromptResult(description=_get(result, "description", None), messages=messages)

    @staticmethod
    def convert_arguments(arguments: Any) -> dict[str, Any] | None:
        return convert_arguments(arguments)


def convert_arguments(arguments: Any) -> dict[str, Any] | None:
    if not isinstance(arguments, dict):
        return None
    return {str(key): _convert_argument_value(value) for key, value in arguments.items()}


def is_unexpected_server_exit(exc: BaseException) -> bool:
    current: BaseException | None = exc
    while current is not None:
        if "mcp server process exited unexpectedly" in str(current).lower():
            return True
        current = current.__cause__ or current.__context__

    message = str(exc).lower()
    return any(
        needle in message
        for needle in (
            "the pipe is broken",
            "the connection is closed",
            "cannot access a disposed object",
        )
    )


def _convert_argument_value(value: Any) -> Any:
    if value is None or isinstance(value, (str, bool, int, float)):
        return value
    if isinstance(value, list):
        return [_convert_argument_value(item) for item in value]
    if isinstance(value, dict):
        return {str(key): _convert_argument_value(item) for key, item in value.items()}
    return value


def _build_content(result: Any) -> Any:
    content = _get(result, "content", None)
    if not isinstance(content, list):
        return content
    if len(content) == 0:
        return None
    if len(content) == 1:
        text = _get(content[0], "text", None)
        if isinstance(text, str):
            try:
                return json.loads(text)
            except Exception:
                return text
    return [
        {"type": _get(block, "type", "text"), **({"text": _get(block, "text", None)} if _get(block, "text", None) is not None else {})}
        for block in content
    ]


def _coerce_tool(value: Any) -> McpToolInfo:
    if isinstance(value, McpToolInfo):
        return value
    return McpToolInfo(
        name=str(_get(value, "name", "")),
        description=_get(value, "description", None),
        input_schema=_get(value, "input_schema", _get(value, "inputSchema", _get(value, "json_schema", None))),
    )


def _coerce_resource(value: Any) -> McpResourceInfo:
    if isinstance(value, McpResourceInfo):
        return value
    return McpResourceInfo(
        uri=str(_get(value, "uri", "")),
        name=str(_get(value, "name", "")),
        description=_get(value, "description", None),
        mime_type=_get(value, "mime_type", _get(value, "mimeType", None)),
    )


def _coerce_prompt(value: Any) -> McpPromptInfo:
    if isinstance(value, McpPromptInfo):
        return value
    return McpPromptInfo(
        name=str(_get(value, "name", "")),
        description=_get(value, "description", None),
        arguments=_get(value, "arguments", None),
    )


def _get(value: Any, key: str, default: Any = None) -> Any:
    if isinstance(value, dict):
        return value.get(key, default)
    return getattr(value, key, default)


def _call_first(client: Any, names: list[str], *args: Any) -> Any:
    for name in names:
        func = getattr(client, name, None)
        if func is not None:
            return func(*args)
    raise WorkflowRuntimeException(ErrorCodes.MCP_CONNECTION_ERROR, f"MCP client does not implement any of: {', '.join(names)}")


def _call_tool_on_client(client: Any, tool_name: str, arguments: dict[str, Any] | None, mcp_meta: dict[str, Any] | None) -> Any:
    for name in ["call_tool_async", "call_tool"]:
        func = getattr(client, name, None)
        if func is None:
            continue

        if not mcp_meta:
            return func(tool_name, arguments)

        try:
            signature = inspect.signature(func)
        except (TypeError, ValueError):
            return func(tool_name, arguments)

        parameters = signature.parameters
        if any(p.kind == inspect.Parameter.VAR_KEYWORD for p in parameters.values()):
            return func(tool_name, arguments, meta=mcp_meta)
        if "mcp_meta" in parameters:
            return func(tool_name, arguments, mcp_meta=mcp_meta)
        if "meta" in parameters:
            return func(tool_name, arguments, meta=mcp_meta)
        if "_meta" in parameters:
            return func(tool_name, arguments, _meta=mcp_meta)

        # Some MCP client wrappers expose the protocol request shape as the second argument.
        # Preserve the original arguments under `arguments` and add top-level `_meta` only for
        # clients that explicitly name their parameter like a full request object.
        request_parameter_names = {"request", "params", "request_params"}
        positional = [p for p in parameters.values() if p.kind in (inspect.Parameter.POSITIONAL_ONLY, inspect.Parameter.POSITIONAL_OR_KEYWORD)]
        if len(positional) >= 2 and positional[1].name in request_parameter_names:
            return func(tool_name, {"arguments": arguments or {}, "_meta": mcp_meta})

        return func(tool_name, arguments)

    raise WorkflowRuntimeException(ErrorCodes.MCP_CONNECTION_ERROR, "MCP client does not implement any of: call_tool_async, call_tool")


async def _maybe_await(value: Any) -> Any:
    if inspect.isawaitable(value):
        return await value
    return value




