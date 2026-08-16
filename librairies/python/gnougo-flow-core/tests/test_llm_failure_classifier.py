from __future__ import annotations

import asyncio

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import ErrorCodes, WorkflowRuntimeException
from gnougo_flow_core.llm_failure_classifier import classify_llm_failure
from gnougo_flow_core.models import McpServerMetadata
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


class _HttpFailure(Exception):
    def __init__(self, status_code: int, message: str = "provider failure") -> None:
        super().__init__(message)
        self.status_code = status_code


class _Response:
    def __init__(self, status_code: int) -> None:
        self.status_code = status_code


class _ResponseFailure(Exception):
    def __init__(self, status_code: int) -> None:
        super().__init__("response failure")
        self.response = _Response(status_code)


class _FailingLlm:
    def __init__(self, failure: Exception) -> None:
        self.failure = failure
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        raise self.failure


class _McpFactory:
    server_metadata = [McpServerMetadata(name="demo", description="Demo tools")]

    async def get_client_async(self, server_name):
        raise AssertionError("MCP discovery must not run after the prefilter provider fails")


def _llm_call_workflow():
    document = WorkflowParser.parse(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: ask
                type: llm.call
                input:
                  model: fake
                  prompt: classify this failure
        """
    )
    return WorkflowCompiler().compile(document).workflows["main"]


@pytest.mark.parametrize(
    ("status", "expected_code", "retryable"),
    [
        (408, ErrorCodes.LLM_TIMEOUT, True),
        (504, ErrorCodes.LLM_TIMEOUT, True),
        (425, ErrorCodes.LLM_NETWORK, True),
        (429, ErrorCodes.LLM_NETWORK, True),
        (500, ErrorCodes.LLM_NETWORK, True),
        (503, ErrorCodes.LLM_NETWORK, True),
        (400, ErrorCodes.LLM_PROVIDER, False),
        (401, ErrorCodes.LLM_PROVIDER, False),
        (404, ErrorCodes.LLM_PROVIDER, False),
    ],
)
def test_classifier_uses_provider_status_contract(status: int, expected_code: str, retryable: bool) -> None:
    classified = classify_llm_failure(_HttpFailure(status))

    assert classified is not None
    assert classified.code == expected_code
    assert classified.retryable is retryable


def test_classifier_reads_response_status_and_transport_failures() -> None:
    response_failure = classify_llm_failure(_ResponseFailure(429))
    transport_failure = classify_llm_failure(ConnectionError("connection reset"))

    assert response_failure is not None
    assert response_failure.code == ErrorCodes.LLM_NETWORK
    assert response_failure.retryable is True
    assert transport_failure is not None
    assert transport_failure.code == ErrorCodes.LLM_NETWORK
    assert transport_failure.retryable is True


def test_classifier_maps_timeout_and_preserves_chained_details() -> None:
    timeout = classify_llm_failure(TimeoutError("timed out"))
    outer = WorkflowRuntimeException(
        ErrorCodes.CAPABILITY_PREFLIGHT_INFERENCE_FAILED,
        "inference failed",
        details={"phase": "capability_inference", "inference_phase": "capability_inventory_call"},
    )
    outer.__cause__ = _HttpFailure(429)
    chained = classify_llm_failure(outer)

    assert timeout is not None
    assert timeout.code == ErrorCodes.LLM_TIMEOUT
    assert timeout.retryable is True
    assert chained is not None
    assert chained.code == ErrorCodes.LLM_NETWORK
    assert chained.retryable is True
    assert chained.details == outer.details
    assert chained.details is not outer.details


def test_classifier_preserves_stable_errors_and_ignores_cancellation() -> None:
    existing = WorkflowRuntimeException(ErrorCodes.LLM_PROVIDER, "rejected")

    assert classify_llm_failure(existing) is existing
    assert classify_llm_failure(asyncio.CancelledError()) is None


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("status", "expected_code", "retryable"),
    [
        (408, ErrorCodes.LLM_TIMEOUT, True),
        (429, ErrorCodes.LLM_NETWORK, True),
        (401, ErrorCodes.LLM_PROVIDER, False),
    ],
)
async def test_llm_call_classifies_provider_failures(status: int, expected_code: str, retryable: bool) -> None:
    engine = WorkflowEngine()
    engine.llm_client = _FailingLlm(_HttpFailure(status))

    result = await engine.execute_async(_llm_call_workflow(), {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == expected_code
    assert result.error.retryable is retryable


@pytest.mark.asyncio
async def test_llm_call_preserves_existing_runtime_error() -> None:
    engine = WorkflowEngine()
    engine.llm_client = _FailingLlm(WorkflowRuntimeException(ErrorCodes.LLM_SCHEMA, "invalid schema"))

    result = await engine.execute_async(_llm_call_workflow(), {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == ErrorCodes.LLM_SCHEMA


@pytest.mark.asyncio
async def test_workflow_plan_auto_timeout_propagates_without_basic_fallback() -> None:
    workflow = WorkflowCompiler().compile(
        WorkflowParser.parse(
            """
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      generator:
                        model: fake
                        instruction: Build a workflow
            """
        )
    ).workflows["main"]
    llm = _FailingLlm(TimeoutError("classification timed out"))
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(workflow, {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == ErrorCodes.LLM_TIMEOUT
    assert result.error.retryable is True
    assert len(llm.requests) == 1
    assert llm.requests[0].use_background_mode is True


@pytest.mark.asyncio
async def test_workflow_plan_mcp_prefilter_provider_failure_does_not_fall_back() -> None:
    workflow = WorkflowCompiler().compile(
        WorkflowParser.parse(
            """
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
                      generator:
                        model: fake
                        instruction: Build a workflow using demo tools
                        prefilter: true
            """
        )
    ).workflows["main"]
    llm = _FailingLlm(_HttpFailure(429))
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = _McpFactory()

    result = await engine.execute_async(workflow, {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == ErrorCodes.LLM_NETWORK
    assert result.error.retryable is True
    assert len(llm.requests) == 1
    assert llm.requests[0].use_background_mode is True
