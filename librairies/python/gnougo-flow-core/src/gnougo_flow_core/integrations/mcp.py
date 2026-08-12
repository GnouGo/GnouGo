from __future__ import annotations

import asyncio
import contextlib
import contextvars
import inspect
import json
import uuid
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Awaitable, Callable, Iterator

from ..errors import ErrorCodes, WorkflowRuntimeException
from ..models import (
    HumanInputFieldDef,
    HumanInputRequest,
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
            McpServerMetadata(
                name=name,
                description=config.description,
                discovery_timeout_seconds=None,
                call_timeout_seconds=None,
            )
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
    discovery_timeout_seconds: int | None = None
    call_timeout_seconds: int | None = None
    client: Any = None


@dataclass(frozen=True, slots=True)
class McpRealtimeProgressEvent:
    message: str | None = None
    level: str | None = None
    event_kind: str | None = None
    file: str | None = None
    timestamp: str | None = None
    correlation_id: str | None = None
    run_id: str | None = None
    step_id: str | None = None
    step_type: str | None = None
    server_name: str | None = None
    method_name: str | None = None
    kind: str | None = None


class McpHumanInputSignalPhase(str, Enum):
    WAITING = "waiting"
    RESUMED = "resumed"
    REFUSED = "refused"
    CANCELLED = "cancelled"


@dataclass(frozen=True, slots=True)
class McpHumanInputSignal:
    correlation: Any
    request: HumanInputRequest
    phase: McpHumanInputSignalPhase


@dataclass(frozen=True, slots=True)
class _ProgressSubscription:
    correlation: Any
    handler: Callable[[McpRealtimeProgressEvent], None]


_progress_handlers: contextvars.ContextVar[tuple[_ProgressSubscription, ...]] = contextvars.ContextVar(
    "gnougo_mcp_progress_handlers",
    default=(),
)

_human_input_handlers: dict[str, _ProgressSubscription] = {}


class ConfiguredMcpClientFactory:
    """Configured MCP factory.

    The Python port keeps the same testable surface as the .NET implementation.
    Real transports can be injected through `McpServerOptions.client`; otherwise
    attempting to open a transport raises a clear integration error rather than
    adding a mandatory third-party dependency to the core package.
    """

    def __init__(
        self,
        server_configs: dict[str, McpServerOptions | dict[str, Any]],
        human_input_provider: Any = None,
    ) -> None:
        self._server_configs = {
            name: (cfg if isinstance(cfg, McpServerOptions) else _coerce_server_options(cfg))
            for name, cfg in server_configs.items()
        }
        self._clients: dict[str, Any] = {}
        self._sessions: dict[str, McpSessionAdapter] = {}
        self._client_locks: dict[str, asyncio.Lock] = {}
        self.human_input_provider = human_input_provider

    @property
    def server_metadata(self) -> list[McpServerMetadata]:
        return [
            McpServerMetadata(
                name=name,
                description=config.description,
                discovery_timeout_seconds=config.discovery_timeout_seconds,
                call_timeout_seconds=config.call_timeout_seconds,
            )
            for name, config in self._server_configs.items()
        ]

    async def get_client_async(self, server_name: str) -> "McpSessionAdapter":
        if server_name not in self._server_configs:
            available = ", ".join(self._server_configs.keys())
            raise WorkflowRuntimeException(
                ErrorCodes.MCP_SERVER_NOT_FOUND,
                f"MCP server '{server_name}' not found. Available: [{available}]",
            )

        lock = self._client_locks.setdefault(server_name, asyncio.Lock())
        async with lock:
            session = self._sessions.get(server_name)
            if session is None:
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
                self._install_elicitation_handler(config.client, server_name)
                session = McpSessionAdapter(server_name, config.client)
                self._sessions[server_name] = session
            await session.ensure_tools_discovered_async()
            return session

    async def dispose_async(self) -> None:
        for client in self._clients.values():
            close = getattr(client, "dispose_async", None) or getattr(client, "aclose", None) or getattr(client, "close", None)
            if close is None:
                continue
            value = close()
            if inspect.isawaitable(value):
                await value
        self._clients.clear()
        self._sessions.clear()
        self._client_locks.clear()

    @staticmethod
    def is_unexpected_server_exit(exc: BaseException) -> bool:
        return is_unexpected_server_exit(exc)

    @staticmethod
    @contextlib.contextmanager
    def push_progress_handler(correlation: Any, handler: Callable[[McpRealtimeProgressEvent], None]) -> Iterator[None]:
        current = _progress_handlers.get()
        token = _progress_handlers.set((*current, _ProgressSubscription(correlation, handler)))
        try:
            yield
        finally:
            _progress_handlers.reset(token)

    @staticmethod
    def capture_stdio_error_line(server_name: str | None, line: str) -> bool:
        try:
            payload = json.loads(line)
        except Exception:
            return False

        if not isinstance(payload, dict) or payload.get("type") != "gnougo.mcp.progress":
            return False

        event_payload = payload.get("event")
        if not isinstance(event_payload, dict):
            return False

        progress_event = McpRealtimeProgressEvent(
            message=_string_or_none(event_payload.get("message") or event_payload.get("Message")),
            level=_string_or_none(event_payload.get("level") or event_payload.get("Level")),
            event_kind=_string_or_none(event_payload.get("kind") or event_payload.get("Kind")),
            file=_string_or_none(event_payload.get("file") or event_payload.get("File")),
            timestamp=_string_or_none(event_payload.get("timestamp") or event_payload.get("Timestamp")),
            correlation_id=_string_or_none(payload.get("correlationId") or payload.get("correlation_id") or payload.get("CorrelationId")),
            run_id=_string_or_none(payload.get("runId") or payload.get("run_id") or payload.get("RunId")),
            step_id=_string_or_none(payload.get("stepId") or payload.get("step_id") or payload.get("StepId")),
            step_type=_string_or_none(payload.get("stepType") or payload.get("step_type") or payload.get("StepType")),
            server_name=_string_or_none(payload.get("server") or payload.get("Server") or server_name),
            method_name=_string_or_none(payload.get("method") or payload.get("Method")),
            kind=_string_or_none(payload.get("kind") or payload.get("Kind")),
        )
        return ConfiguredMcpClientFactory.publish_progress(progress_event)

    @staticmethod
    def publish_progress(progress_event: McpRealtimeProgressEvent) -> bool:
        delivered = False
        for subscription in _progress_handlers.get():
            if not _progress_matches(subscription.correlation, progress_event):
                continue
            try:
                subscription.handler(progress_event)
                delivered = True
            except Exception:
                pass
        return delivered

    @staticmethod
    @contextlib.contextmanager
    def push_human_input_handler(correlation: Any, handler: Callable[[McpHumanInputSignal], None]) -> Iterator[None]:
        token = f"{id(correlation)}:{id(handler)}:{len(_human_input_handlers)}"
        _human_input_handlers[token] = _ProgressSubscription(correlation, handler)
        try:
            yield
        finally:
            _human_input_handlers.pop(token, None)

    @staticmethod
    def publish_human_input(signal: McpHumanInputSignal) -> bool:
        delivered = False
        for subscription in list(_human_input_handlers.values()):
            if not _correlation_matches(subscription.correlation, signal.correlation):
                continue
            try:
                subscription.handler(signal)
                delivered = True
            except Exception:
                pass
        return delivered

    def _install_elicitation_handler(self, client: Any, server_name: str) -> None:
        async def handler(request: Any) -> Any:
            return await self.handle_elicitation_async(request, server_name)

        setter = getattr(client, "set_elicitation_handler", None)
        if callable(setter):
            setter(handler)
        elif hasattr(client, "elicitation_handler"):
            client.elicitation_handler = handler

    async def handle_elicitation_async(self, request: Any, server_name: str | None = None) -> dict[str, Any]:
        provider = self.human_input_provider
        if provider is None:
            return {"action": "decline", "content": None}
        correlation = _resolve_elicitation_correlation(request, server_name)
        if correlation is None:
            raise WorkflowRuntimeException(
                ErrorCodes.MCP_CALL_ERROR,
                "MCP elicitation arrived without call correlation metadata while multiple calls were active.",
            )
        human_request = _build_elicitation_human_request(request, correlation)
        self.publish_human_input(McpHumanInputSignal(correlation, human_request, McpHumanInputSignalPhase.WAITING))
        try:
            response = await provider.request_input_async(human_request)
        except (asyncio.CancelledError, TimeoutError):
            self.publish_human_input(McpHumanInputSignal(correlation, human_request, McpHumanInputSignalPhase.CANCELLED))
            raise

        if _is_refused_elicitation_response(response):
            self.publish_human_input(McpHumanInputSignal(correlation, human_request, McpHumanInputSignalPhase.REFUSED))
            return {"action": "decline", "content": None}
        content = _normalize_elicitation_response(response, human_request)
        self.publish_human_input(McpHumanInputSignal(correlation, human_request, McpHumanInputSignalPhase.RESUMED))
        return {"action": "accept", "content": content}


class McpSessionAdapter:
    def __init__(self, server_name: str, client: Any) -> None:
        self.server_name = server_name
        self._client = client
        self._tools: list[McpToolInfo] | None = None
        self._tools_lock = asyncio.Lock()

    async def ensure_tools_discovered_async(self) -> list[McpToolInfo]:
        if self._tools is not None:
            return list(self._tools)
        async with self._tools_lock:
            if self._tools is None:
                tools = await _maybe_await(_call_first(self._client, ["list_tools_async", "list_tools"]))
                self._tools = [_coerce_tool(tool) for tool in (tools or [])]
        return list(self._tools)

    async def list_tools_async(self) -> list[McpToolInfo]:
        return await self.ensure_tools_discovered_async()

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


def _coerce_server_options(values: dict[str, Any]) -> McpServerOptions:
    normalized = dict(values)
    for target, aliases in {
        "discovery_timeout_seconds": ("DiscoveryTimeoutSeconds", "discoveryTimeoutSeconds", "discovery_timeout_seconds"),
        "call_timeout_seconds": ("CallTimeoutSeconds", "callTimeoutSeconds", "call_timeout_seconds"),
        "api_key": ("ApiKey", "apiKey", "api_key"),
    }.items():
        if target in normalized:
            continue
        for alias in aliases:
            if alias in normalized:
                normalized[target] = normalized[alias]
                break
    allowed = set(McpServerOptions.__dataclass_fields__.keys())
    return McpServerOptions(**{key: value for key, value in normalized.items() if key in allowed})


def _string_or_none(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _progress_matches(correlation: Any, progress_event: McpRealtimeProgressEvent) -> bool:
    for progress_value, correlation_names in (
        (progress_event.run_id, ("run_id", "RunId")),
        (progress_event.step_id, ("step_id", "StepId")),
        (progress_event.step_type, ("step_type", "StepType")),
        (progress_event.server_name, ("mcp_server", "server_name", "ServerName")),
        (progress_event.kind, ("mcp_kind", "kind", "Kind")),
    ):
        if progress_value is None:
            continue
        expected = _first_attr(correlation, correlation_names)
        if expected is not None and str(expected).lower() != progress_value.lower():
            return False

    if progress_event.method_name:
        expected_method = _first_attr(correlation, ("mcp_method", "method_name", "MethodName"))
        if expected_method and progress_event.method_name not in {part.strip() for part in str(expected_method).split(",") if part.strip()}:
            return False

    return True


def _correlation_matches(expected: Any, actual: Any) -> bool:
    expected_id = _first_attr(expected, ("correlation_id", "CorrelationId"))
    actual_id = _first_attr(actual, ("correlation_id", "CorrelationId"))
    if expected_id and actual_id and str(expected_id).lower() != str(actual_id).lower():
        return False
    for names in (
        ("run_id", "RunId"),
        ("step_id", "StepId"),
        ("mcp_server", "server_name", "ServerName"),
        ("mcp_method", "method_name", "MethodName"),
    ):
        left = _first_attr(expected, names)
        right = _first_attr(actual, names)
        if left and right and str(left).lower() != str(right).lower():
            return False
    return True


def _resolve_elicitation_correlation(request: Any, server_name: str | None) -> Any | None:
    meta = _get(request, "meta", _get(request, "_meta", None))
    gnougo = _get(meta, "gnougo", None) if meta is not None else None
    candidates = [subscription.correlation for subscription in _human_input_handlers.values()]
    if isinstance(gnougo, dict):
        requested = {
            "correlation_id": _get(gnougo, "correlationId", _get(gnougo, "correlation_id", None)),
            "run_id": _get(gnougo, "runId", None),
            "step_id": _get(gnougo, "stepId", None),
            "mcp_server": _get(gnougo, "mcpServer", server_name),
            "mcp_method": _get(gnougo, "mcpMethod", None),
        }
        matches = [candidate for candidate in candidates if _correlation_matches(candidate, requested)]
        return matches[0] if len(matches) == 1 else None
    if server_name:
        candidates = [
            candidate for candidate in candidates
            if not _first_attr(candidate, ("mcp_server", "server_name", "ServerName"))
            or str(_first_attr(candidate, ("mcp_server", "server_name", "ServerName"))).lower()
            == server_name.lower()
        ]
    unique = {id(candidate): candidate for candidate in candidates}
    return next(iter(unique.values())) if len(unique) == 1 else None


def _build_elicitation_human_request(request: Any, correlation: Any) -> HumanInputRequest:
    message = str(_get(request, "message", "MCP tool requires additional input."))
    schema = _get(request, "requested_schema", _get(request, "requestedSchema", {}))
    properties = _get(schema, "properties", {}) if schema is not None else {}
    required = set(_get(schema, "required", []) or [])
    fields: list[HumanInputFieldDef] = []
    if isinstance(properties, dict):
        for name, definition in properties.items():
            enum_values = _get(definition, "enum", None)
            type_name = str(_get(definition, "type", "string"))
            fields.append(
                HumanInputFieldDef(
                    name=str(name),
                    type="select" if isinstance(enum_values, list) and enum_values else type_name,
                    required=str(name) in required,
                    description=_get(definition, "description", None),
                    options=[str(item) for item in enum_values] if isinstance(enum_values, list) else None,
                )
            )
    mode = "form"
    choices = None
    if len(fields) == 1 and fields[0].options:
        mode = "choice"
        choices = list(fields[0].options)
    return HumanInputRequest(
        run_id=str(_first_attr(correlation, ("run_id", "RunId")) or uuid.uuid4().hex),
        step_id=str(_first_attr(correlation, ("step_id", "StepId")) or "mcp.elicitation"),
        prompt=message,
        mode=mode,
        choices=choices,
        fields=None if mode == "choice" else fields,
    )


def _is_refused_elicitation_response(response: Any) -> bool:
    if not isinstance(response, dict):
        return False
    action = str(response.get("action", "")).lower()
    return action in {"decline", "refuse", "refused", "reject", "cancel", "cancelled"}


def _normalize_elicitation_response(response: Any, request: HumanInputRequest) -> dict[str, Any]:
    if isinstance(response, dict):
        clean = {
            str(key): value for key, value in response.items()
            if key not in {"source", "action", "run_id", "step_id"}
        }
        if request.mode == "choice" and request.fields is None and "response" in clean:
            return {"answer": clean["response"]}
        return clean
    if request.mode == "choice":
        return {"answer": response}
    if request.fields and len(request.fields) == 1:
        return {request.fields[0].name: response}
    return {"response": response}


def _first_attr(value: Any, names: tuple[str, ...]) -> Any:
    for name in names:
        if isinstance(value, dict):
            current = value.get(name)
        else:
            current = getattr(value, name, None)
        if current is not None and str(current).strip():
            return current
    return None


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
        meta=_get(value, "meta", _get(value, "_meta", None)),
        outputSchema=_get(value, "output_schema", _get(value, "outputSchema", None)),
        exampleResponse=_get(value, "example_response", _get(value, "exampleResponse", None)),
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




