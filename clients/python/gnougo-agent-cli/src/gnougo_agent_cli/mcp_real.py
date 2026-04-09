from __future__ import annotations

import asyncio
from contextlib import AsyncExitStack
from typing import Any

from mcp import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client

from gnougo_flow_core.models import (
    McpCallResult,
    McpGetPromptResult,
    McpPromptArgument,
    McpPromptInfo,
    McpPromptMessage,
    McpResourceInfo,
    McpServerMetadata,
    McpToolInfo,
)

from .settings import McpServerSettings


class RealMcpSession:
    def __init__(self, server_name: str, settings: McpServerSettings) -> None:
        self.server_name = server_name
        self._settings = settings
        self._lock = asyncio.Lock()
        self._stack: AsyncExitStack | None = None
        self._session: ClientSession | None = None

    async def _connect(self) -> ClientSession:
        if not self._settings.command:
            raise ValueError(f"MCP server '{self.server_name}' has no command configured")

        if self._session is not None:
            return self._session

        params = StdioServerParameters(
            command=self._settings.command,
            args=list(self._settings.args or []),
            env=dict(self._settings.env or {}),
            cwd=self._settings.cwd,
        )

        stack = AsyncExitStack()
        read_stream, write_stream = await stack.enter_async_context(stdio_client(params))
        session = await stack.enter_async_context(ClientSession(read_stream, write_stream))
        await session.initialize()

        self._stack = stack
        self._session = session
        return session

    async def _reset(self) -> None:
        self._session = None
        if self._stack is not None:
            await self._stack.aclose()
            self._stack = None

    async def aclose(self) -> None:
        async with self._lock:
            await self._reset()

    async def _with_session(self, op):
        async with self._lock:
            session = await self._connect()
            try:
                return await op(session)
            except Exception:
                # Force reconnect on next operation if the transport/session became unhealthy.
                await self._reset()
                raise

    async def list_tools_async(self) -> list[McpToolInfo]:
        async def _op(session: ClientSession):
            result = await session.list_tools()
            tools: list[McpToolInfo] = []
            for t in result.tools:
                tools.append(
                    McpToolInfo(
                        name=getattr(t, "name", ""),
                        description=getattr(t, "description", None),
                        input_schema=getattr(t, "inputSchema", None),
                    )
                )
            return tools

        return await self._with_session(_op)

    async def list_resources_async(self) -> list[McpResourceInfo]:
        async def _op(session: ClientSession):
            result = await session.list_resources()
            resources: list[McpResourceInfo] = []
            for r in result.resources:
                resources.append(
                    McpResourceInfo(
                        uri=str(getattr(r, "uri", "")),
                        name=getattr(r, "name", ""),
                        description=getattr(r, "description", None),
                        mime_type=getattr(r, "mimeType", None),
                    )
                )
            return resources

        return await self._with_session(_op)

    async def list_prompts_async(self) -> list[McpPromptInfo]:
        async def _op(session: ClientSession):
            result = await session.list_prompts()
            prompts: list[McpPromptInfo] = []
            for p in result.prompts:
                args = None
                raw_args = getattr(p, "arguments", None)
                if raw_args:
                    args = [
                        McpPromptArgument(
                            name=getattr(a, "name", ""),
                            description=getattr(a, "description", None),
                            required=bool(getattr(a, "required", False)),
                        )
                        for a in raw_args
                    ]
                prompts.append(
                    McpPromptInfo(
                        name=getattr(p, "name", ""),
                        description=getattr(p, "description", None),
                        arguments=args,
                    )
                )
            return prompts

        return await self._with_session(_op)

    async def call_tool_async(self, tool_name: str, arguments: Any) -> McpCallResult:
        async def _op(session: ClientSession):
            timeout = None
            result = await session.call_tool(name=tool_name, arguments=arguments if isinstance(arguments, dict) else None, read_timeout_seconds=timeout)
            content_blocks = []
            for block in result.content:
                if hasattr(block, "model_dump"):
                    content_blocks.append(block.model_dump())
                else:
                    content_blocks.append(str(block))
            return McpCallResult(
                is_error=bool(getattr(result, "isError", False)),
                content=content_blocks,
                usage=None,
                model=None,
            )

        return await self._with_session(_op)

    async def get_prompt_async(self, prompt_name: str, arguments: Any) -> McpGetPromptResult:
        async def _op(session: ClientSession):
            req_args = arguments if isinstance(arguments, dict) else None
            result = await session.get_prompt(name=prompt_name, arguments=req_args)
            messages: list[McpPromptMessage] = []
            for m in result.messages:
                content_obj = getattr(m, "content", None)
                text = ""
                if content_obj is not None:
                    text = getattr(content_obj, "text", None) or str(content_obj)
                messages.append(McpPromptMessage(role=getattr(m, "role", "user"), content=text))
            return McpGetPromptResult(
                description=getattr(result, "description", None),
                messages=messages,
                usage=None,
                model=None,
            )

        return await self._with_session(_op)


class RealMcpFactory:
    def __init__(self, servers: dict[str, McpServerSettings]) -> None:
        self._servers = {k: v for k, v in servers.items() if (v.type or "stdio").lower() == "stdio"}
        self._sessions: dict[str, RealMcpSession] = {}
        self._lock = asyncio.Lock()
        self.server_metadata = [
            McpServerMetadata(name=name, description=cfg.description)
            for name, cfg in self._servers.items()
        ]

    async def get_client_async(self, server_name: str) -> RealMcpSession:
        async with self._lock:
            if server_name in self._sessions:
                return self._sessions[server_name]
            cfg = self._servers.get(server_name)
            if cfg is None:
                raise ValueError(f"Unknown MCP server '{server_name}'")
            session = RealMcpSession(server_name, cfg)
            self._sessions[server_name] = session
            return session

    async def aclose(self) -> None:
        async with self._lock:
            sessions = list(self._sessions.values())
            self._sessions.clear()
        for session in sessions:
            await session.aclose()

    def has_servers(self) -> bool:
        return bool(self._servers)

