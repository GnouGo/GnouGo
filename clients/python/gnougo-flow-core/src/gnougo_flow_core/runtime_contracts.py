from __future__ import annotations

from typing import Any, Protocol

from .models import (
    HumanInputRequest,
    LLMRequest,
    LLMResponse,
    McpCallResult,
    McpGetPromptResult,
    McpPromptInfo,
    McpResourceInfo,
    McpServerMetadata,
    McpToolInfo,
    TemplateResult,
)


class ILLMClient(Protocol):
    async def call_async(self, request: LLMRequest) -> LLMResponse: ...


class IWorkflowFetcher(Protocol):
    async def fetch_async(self, url: str, integrity: str | None) -> str: ...


class ITemplateEngine(Protocol):
    async def render_async(self, template: str, data: Any, strict: bool, mode: str) -> TemplateResult: ...


class IMcpSession(Protocol):
    server_name: str

    async def list_tools_async(self) -> list[McpToolInfo]: ...

    async def list_resources_async(self) -> list[McpResourceInfo]: ...

    async def list_prompts_async(self) -> list[McpPromptInfo]: ...

    async def call_tool_async(self, tool_name: str, arguments: Any) -> McpCallResult: ...

    async def get_prompt_async(self, prompt_name: str, arguments: Any) -> McpGetPromptResult: ...


class IMcpClientFactory(Protocol):
    server_metadata: list[McpServerMetadata]

    async def get_client_async(self, server_name: str) -> IMcpSession: ...


class IHumanInputProvider(Protocol):
    async def request_input_async(self, request: HumanInputRequest) -> Any: ...


class ITelemetrySpan:
    def set_attribute(self, key: str, value: Any) -> None:
        return

    def add_event(self, name: str, attributes: list[tuple[str, Any]] | None = None) -> None:
        return

    def end(self) -> None:
        return


class IWorkflowTelemetry:
    def workflow_start(self, info: dict[str, Any]) -> ITelemetrySpan:
        return ITelemetrySpan()

    def workflow_end(self, span: ITelemetrySpan, info: dict[str, Any]) -> None:
        return

    def step_start(self, parent: ITelemetrySpan, info: dict[str, Any]) -> ITelemetrySpan:
        return ITelemetrySpan()

    def step_end(self, span: ITelemetrySpan, info: dict[str, Any]) -> None:
        return


class NullWorkflowTelemetry(IWorkflowTelemetry):
    pass

