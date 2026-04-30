"""Parity tests for the .NET `LLMRequest.Reasoning` field and its defaults."""

from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMRequest, LLMResponse, WorkflowDocument
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.reasoning import (
    normalize_ollama_think,
    normalize_openai_reasoning,
)
from gnougo_flow_core.runtime import WorkflowEngine

# -- Model field round-trip ------------------------------------------------


def test_llm_request_has_optional_reasoning_field() -> None:
    req = LLMRequest(model="gpt-4", prompt="hi")
    assert req.reasoning is None

    req2 = LLMRequest(model="gpt-4", prompt="hi", reasoning="high")
    assert req2.reasoning == "high"


# -- Normalization helpers (mirror ChatRequestBuilder) --------------------


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        (None, None),
        ("", None),
        ("auto", None),
        ("unknown", None),
        ("minimal", "minimal"),
        ("MIN", "minimal"),
        ("low", "low"),
        ("Medium", "medium"),
        ("MED", "medium"),
        ("high", "high"),
        ("max", "high"),
        ("MAXIMUM", "high"),
    ],
)
def test_normalize_openai_reasoning(value, expected) -> None:
    assert normalize_openai_reasoning(value) == expected


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        (None, None),
        ("", None),
        ("auto", None),
        ("unknown", None),
        ("none", False),
        ("OFF", False),
        ("false", False),
        ("0", False),
        ("low", True),
        ("medium", True),
        ("high", True),
        ("max", True),
        ("true", True),
        ("1", True),
    ],
)
def test_normalize_ollama_think(value, expected) -> None:
    assert normalize_ollama_think(value) == expected


# -- Document version/dsl alias parity ------------------------------------


def test_workflow_document_accepts_dsl_or_version_key() -> None:
    yaml_with_dsl = """
    version: 1
    workflows:
      main:
        steps:
          - id: noop
            type: set
            input: {}
    """
    yaml_with_version = """
    version: 1
    workflows:
      main:
        steps:
          - id: noop
            type: set
            input: {}
    """

    d1 = WorkflowParser.parse(yaml_with_dsl)
    d2 = WorkflowParser.parse(yaml_with_version)
    assert d1.version == 1
    assert d2.version == 1


def test_workflow_document_model_accepts_both_field_names() -> None:
    by_alias = WorkflowDocument.model_validate({"dsl": 1, "workflows": {}})
    by_name = WorkflowDocument(version=1)
    assert by_alias.version == 1
    assert by_name.version == 1


# -- llm.call forwards `reasoning` through the request --------------------


class _CapturingLlm:
    def __init__(self) -> None:
        self.requests: list[LLMRequest] = []

    async def call_async(self, request: LLMRequest) -> LLMResponse:
        self.requests.append(request)
        return LLMResponse(text="ok")


@pytest.mark.asyncio
async def test_llm_call_forwards_reasoning_field() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: ask
            type: llm.call
            input:
              model: gpt-4
              prompt: "hello"
              reasoning: medium
    """
    doc = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(doc)
    llm = _CapturingLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(compiled.workflows["main"], inputs=None)
    assert result.success
    assert llm.requests, "llm.call should have invoked the LLM client"
    assert llm.requests[0].reasoning == "medium"


@pytest.mark.asyncio
async def test_llm_call_omits_reasoning_when_not_specified() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: ask
            type: llm.call
            input:
              model: gpt-4
              prompt: "hello"
    """
    doc = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(doc)
    llm = _CapturingLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm

    await engine.execute_async(compiled.workflows["main"], inputs=None)
    assert llm.requests[0].reasoning is None


# -- workflow.plan defaults reasoning to "high" ---------------------------


@pytest.mark.asyncio
async def test_workflow_plan_defaults_reasoning_to_high() -> None:
    plan_yaml = """
    version: 1
    workflows:
      generated:
        steps:
          - id: noop
            type: set
            input: {}
    """
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build something"
              policy:
                denied_step_types: [workflow.plan]
    """
    doc = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(doc)

    class _PlanLlm:
        def __init__(self) -> None:
            self.requests: list[LLMRequest] = []

        async def call_async(self, request: LLMRequest) -> LLMResponse:
            self.requests.append(request)
            return LLMResponse(text=plan_yaml)

    llm = _PlanLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm
    result = await engine.execute_async(compiled.workflows["main"], inputs=None)
    assert result.success
    assert llm.requests, "workflow.plan should have called the LLM"
    # Every LLM call (main attempt + any prefilter) defaults to "high".
    assert all(r.reasoning == "high" for r in llm.requests), [r.reasoning for r in llm.requests]


@pytest.mark.asyncio
async def test_workflow_plan_respects_explicit_reasoning_override() -> None:
    plan_yaml = """
    version: 1
    workflows:
      generated:
        steps:
          - id: noop
            type: set
            input: {}
    """
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build something"
                reasoning: low
              policy:
                denied_step_types: [workflow.plan]
    """
    doc = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(doc)

    class _PlanLlm:
        def __init__(self) -> None:
            self.requests: list[LLMRequest] = []

        async def call_async(self, request: LLMRequest) -> LLMResponse:
            self.requests.append(request)
            return LLMResponse(text=plan_yaml)

    llm = _PlanLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm
    await engine.execute_async(compiled.workflows["main"], inputs=None)
    assert all(r.reasoning == "low" for r in llm.requests), [r.reasoning for r in llm.requests]





