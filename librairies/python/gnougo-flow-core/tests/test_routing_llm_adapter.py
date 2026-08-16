from __future__ import annotations

import pytest

from gnougo_flow_core.integrations import RoutingLLMClientAdapter
from gnougo_flow_core.models import LLMRequest, LLMTool


class _RoutingClient:
    def __init__(self) -> None:
        self.request = None

    async def call_async(self, request):
        self.request = request
        return {
            "text": "done",
            "json": {"ok": True},
            "tool_calls": [{"id": "call-1", "name": "search", "arguments": {"query": "flow"}}],
        }


@pytest.mark.asyncio
async def test_adapter_forwards_background_tokens_tools_and_maps_response() -> None:
    inner = _RoutingClient()
    adapter = RoutingLLMClientAdapter(inner)

    response = await adapter.call_async(
        LLMRequest(
            provider="openai",
            model="gpt-test",
            prompt="plan",
            reasoning="high",
            max_tokens=4096,
            use_background_mode=True,
            tools=[LLMTool(name="search", description="Search", input_schema={"type": "object"})],
        )
    )

    assert inner.request["use_background_mode"] is True
    assert inner.request["max_output_tokens"] == 4096
    assert inner.request["tools"] == [
        {"name": "search", "description": "Search", "input_schema": {"type": "object"}}
    ]
    assert response.text == "done"
    assert response.json_payload == {"ok": True}
    assert response.tool_calls is not None
    assert response.tool_calls[0].name == "search"
