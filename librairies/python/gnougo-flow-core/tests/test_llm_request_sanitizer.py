from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMRequest, LLMResponse, WorkflowDocument
from gnougo_flow_core.runtime import WorkflowEngine


class _CapturingLlm:
    def __init__(self) -> None:
        self.requests: list[LLMRequest] = []

    async def call_async(self, request: LLMRequest) -> LLMResponse:
        self.requests.append(request)
        return LLMResponse(text="ok")


@pytest.mark.asyncio
async def test_engine_sanitizes_llm_call_temperature_for_reasoning_model() -> None:
    doc = WorkflowDocument.model_validate(
        {
            "version": 1,
            "workflows": {
                "main": {
                    "steps": [
                        {
                            "id": "ask",
                            "type": "llm.call",
                            "input": {
                                "provider": "openai",
                                "model": "o4-mini",
                                "prompt": "hello",
                                "temperature": 0.7,
                                "reasoning": "high",
                            },
                        }
                    ]
                }
            },
        }
    )
    compiled = WorkflowCompiler().compile(doc)
    llm = _CapturingLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(compiled.workflows["main"], inputs=None)

    assert result.success
    assert llm.requests[0].temperature is None
    assert llm.requests[0].reasoning == "high"


@pytest.mark.asyncio
async def test_engine_keeps_reasoning_for_unknown_fake_client_model() -> None:
    doc = WorkflowDocument.model_validate(
        {
            "version": 1,
            "workflows": {
                "main": {
                    "steps": [
                        {
                            "id": "ask",
                            "type": "llm.call",
                            "input": {"model": "fake", "prompt": "hello", "reasoning": "medium"},
                        }
                    ]
                }
            },
        }
    )
    compiled = WorkflowCompiler().compile(doc)
    llm = _CapturingLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(compiled.workflows["main"], inputs=None)

    assert result.success
    assert llm.requests[0].reasoning == "medium"
