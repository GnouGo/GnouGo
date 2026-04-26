import pytest

from gnougo_flow_core.integrations import (
    ConfiguredMcpClientFactory,
    InMemoryMcpClientFactory,
    McpServerOptions,
    MockMcpServerConfig,
    convert_arguments,
    is_unexpected_server_exit,
)
from gnougo_flow_core.mcp_cache import McpCacheHelper
from gnougo_flow_core.models import McpCallResult, McpResourceInfo, McpToolInfo


def test_is_unexpected_server_exit_returns_true_for_nested_process_exit_message():
    inner = RuntimeError("MCP server process exited unexpectedly Server's stderr tail: ...")
    outer = RuntimeError("outer")
    outer.__cause__ = inner

    assert is_unexpected_server_exit(outer) is True
    assert ConfiguredMcpClientFactory.is_unexpected_server_exit(outer) is True


@pytest.mark.parametrize(
    "message",
    [
        "The pipe is broken.",
        "The connection is closed.",
        "Cannot access a disposed object.",
    ],
)
def test_is_unexpected_server_exit_returns_true_for_known_disconnect_messages(message):
    assert is_unexpected_server_exit(RuntimeError(message)) is True


def test_is_unexpected_server_exit_returns_false_for_unrelated_errors():
    assert is_unexpected_server_exit(RuntimeError("validation failed")) is False


def test_convert_arguments_preserves_arrays_nested_objects_and_scalars():
    result = convert_arguments(
        {
            "name": "slimfaas",
            "schedules": [],
            "metadata": {"enabled": True, "tags": ["web", "summary"]},
            "count": 3,
            "ratio": 0.5,
        }
    )

    assert result == {
        "name": "slimfaas",
        "schedules": [],
        "metadata": {"enabled": True, "tags": ["web", "summary"]},
        "count": 3,
        "ratio": 0.5,
    }


@pytest.mark.asyncio
async def test_in_memory_mcp_factory_lists_and_calls_configured_server():
    factory = InMemoryMcpClientFactory()
    factory.register_server(
        "demo",
        MockMcpServerConfig(
            description="Demo server",
            tools=[McpToolInfo(name="ping", description="Ping")],
            resources=[McpResourceInfo(uri="file:///a.txt", name="a")],
            tool_handlers={
                "ping": lambda args: McpCallResult(
                    is_error=False, content={"pong": True, "args": args}
                )
            },
        ),
    )

    assert factory.server_metadata[0].name == "demo"
    assert factory.server_metadata[0].description == "Demo server"
    session = await factory.get_client_async("demo")
    assert (await session.list_tools_async())[0].name == "ping"
    assert (await session.list_resources_async())[0].name == "a"
    result = await session.call_tool_async("ping", {"x": 1})
    assert result.content == {"pong": True, "args": {"x": 1}}


@pytest.mark.asyncio
async def test_in_memory_mcp_factory_unknown_server_raises_mcp_server_not_found():
    factory = InMemoryMcpClientFactory()
    with pytest.raises(Exception) as exc:
        await factory.get_client_async("missing")
    assert getattr(exc.value, "code", None) == "MCP_SERVER_NOT_FOUND"


class InjectedClient:
    async def list_tools_async(self):
        return [McpToolInfo(name="tool")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, name, arguments):
        return McpCallResult(is_error=False, content={"name": name, "arguments": arguments})


@pytest.mark.asyncio
async def test_configured_mcp_factory_uses_injected_client_adapter():
    factory = ConfiguredMcpClientFactory(
        {"demo": McpServerOptions(description="Injected", client=InjectedClient())}
    )

    assert factory.server_metadata[0].name == "demo"
    session = await factory.get_client_async("demo")
    tools = await session.list_tools_async()
    assert tools[0].name == "tool"
    result = await session.call_tool_async("tool", {"nested": {"items": [1, True]}})
    assert result.content["arguments"] == {"nested": {"items": [1, True]}}


def test_mcp_cache_helper_deep_copies_and_expires():
    cache = McpCacheHelper(ttl_seconds=0.01)
    tools = [McpToolInfo(name="search", input_schema={"properties": {"q": {"type": "string"}}})]
    cache.cache_tools("srv", tools)

    cached = cache.get_cached_tools("srv")
    cached[0].input_schema["properties"]["q"]["type"] = "number"

    assert cache.get_cached_tools("srv")[0].input_schema["properties"]["q"]["type"] == "string"

    import time

    time.sleep(0.02)
    assert cache.get_cached_tools("srv") is None
