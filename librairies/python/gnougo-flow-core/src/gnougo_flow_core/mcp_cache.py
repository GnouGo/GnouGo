from __future__ import annotations

import copy
import time
from dataclasses import dataclass
from typing import Any


@dataclass(slots=True)
class _CacheEntry:
    value: Any
    expires_at: float


class McpCacheHelper:
    """TTL/sliding cache for MCP capability listings.

    Mirrors the .NET `McpCacheHelper` behavior closely enough for the Python
    runtime: tools/resources/prompts are cached per server with a five-minute
    sliding expiration by default. Values are deep-copied on get/set so callers
    cannot mutate the cached copy by accident.
    """

    DEFAULT_TTL_SECONDS = 5 * 60

    @staticmethod
    def tools_key(server_name: str) -> str:
        return f"gnougo-flow:mcp:{server_name}:tools"

    @staticmethod
    def resources_key(server_name: str) -> str:
        return f"gnougo-flow:mcp:{server_name}:resources"

    @staticmethod
    def prompts_key(server_name: str) -> str:
        return f"gnougo-flow:mcp:{server_name}:prompts"

    def __init__(self, ttl_seconds: float | None = None) -> None:
        self.ttl_seconds = float(
            ttl_seconds if ttl_seconds is not None else self.DEFAULT_TTL_SECONDS
        )
        self._entries: dict[str, _CacheEntry] = {}

    def clear(self) -> None:
        self._entries.clear()

    def get(self, key: str) -> Any | None:
        entry = self._entries.get(key)
        now = time.monotonic()
        if entry is None:
            return None
        if entry.expires_at < now:
            self._entries.pop(key, None)
            return None
        entry.expires_at = now + self.ttl_seconds
        return copy.deepcopy(entry.value)

    def set(self, key: str, value: Any) -> None:
        self._entries[key] = _CacheEntry(
            value=copy.deepcopy(value),
            expires_at=time.monotonic() + self.ttl_seconds,
        )

    def get_cached_tools(self, server_name: str) -> Any | None:
        return self.get(self.tools_key(server_name))

    def get_cached_resources(self, server_name: str) -> Any | None:
        return self.get(self.resources_key(server_name))

    def get_cached_prompts(self, server_name: str) -> Any | None:
        return self.get(self.prompts_key(server_name))

    def cache_tools(self, server_name: str, tools: Any) -> None:
        self.set(self.tools_key(server_name), tools)

    def cache_resources(self, server_name: str, resources: Any) -> None:
        self.set(self.resources_key(server_name), resources)

    def cache_prompts(self, server_name: str, prompts: Any) -> None:
        self.set(self.prompts_key(server_name), prompts)


def get_cached_tools(cache: McpCacheHelper | None, server_name: str) -> Any | None:
    return cache.get_cached_tools(server_name) if cache is not None else None


def get_cached_resources(cache: McpCacheHelper | None, server_name: str) -> Any | None:
    return cache.get_cached_resources(server_name) if cache is not None else None


def get_cached_prompts(cache: McpCacheHelper | None, server_name: str) -> Any | None:
    return cache.get_cached_prompts(server_name) if cache is not None else None


def cache_tools(cache: McpCacheHelper | None, server_name: str, tools: Any) -> None:
    if cache is not None:
        cache.cache_tools(server_name, tools)


def cache_resources(cache: McpCacheHelper | None, server_name: str, resources: Any) -> None:
    if cache is not None:
        cache.cache_resources(server_name, resources)


def cache_prompts(cache: McpCacheHelper | None, server_name: str, prompts: Any) -> None:
    if cache is not None:
        cache.cache_prompts(server_name, prompts)
