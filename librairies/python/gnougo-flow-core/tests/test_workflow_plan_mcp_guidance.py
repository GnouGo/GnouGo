from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, McpServerMetadata, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine

_VALID_PLAN_YAML = """
dsl: 1
workflows:
  main:
    steps:
      - id: s
        type: template.render
        input:
          engine: mustache
          template: ok
          mode: text
"""


class _CaptureLlm:
    def __init__(self) -> None:
        self.prompts: list[str] = []

    async def call_async(self, request):
        self.prompts.append(request.prompt)
        return LLMResponse(text=_VALID_PLAN_YAML)


class _ToolSession:
    async def list_tools_async(self):
        return [McpToolInfo(name="list_repos", description="List repos")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments):
        raise NotImplementedError

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class _ToolFactory:
    server_metadata = [
        McpServerMetadata(
            name="github",
            description="GitHub repository automation and file operations",
        )
    ]

    async def get_client_async(self, server_name):
        return _ToolSession()


class _BrokenFactory:
    server_metadata = [McpServerMetadata(name="broken-server", description="A server that fails to connect")]

    async def get_client_async(self, server_name):
        raise RuntimeError("Connection refused")


def _compile_main(yaml_text: str):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


async def _run_plan(factory):
    source = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: gpt-4
                instruction: Build an MCP workflow
              validate:
                compile: false
    """
    llm = _CaptureLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = factory
    result = await engine.execute_async(_compile_main(source), {})
    assert result.success
    return next(prompt for prompt in llm.prompts if "[AVAILABLE STEP TYPES]" in prompt)


@pytest.mark.asyncio
async def test_workflow_plan_prompt_mentions_llm_assisted_and_direct_mcp_call() -> None:
    prompt = await _run_plan(_ToolFactory())

    assert "use mcp.call with prompt + model (+ optional temperature)" in prompt
    assert "put the natural-language instruction in input.prompt" in prompt
    assert "Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly" in prompt
    assert "list_repos" in prompt


@pytest.mark.asyncio
async def test_workflow_plan_prompt_falls_back_when_mcp_discovery_fails() -> None:
    prompt = await _run_plan(_BrokenFactory())

    assert "[AVAILABLE MCP SERVERS]" in prompt
    assert "- broken-server: A server that fails to connect" in prompt
    assert "(tool discovery unavailable)" in prompt
    assert "Required MCP planning pattern: discover candidate servers" in prompt


def test_mcp_executors_dsl_snippets_contain_llm_assisted_patterns() -> None:
    engine = WorkflowEngine()

    mcp_call = engine.registry.get("mcp.call")
    assert mcp_call is not None
    assert "LLM-assisted MCP call pattern:" in mcp_call.dsl_snippet
    assert "provide a natural-language `prompt` + `model` (+ optional `temperature`)" in mcp_call.dsl_snippet
    assert "tools: \"${data.steps.discover_mcp.tools}\"" in mcp_call.dsl_snippet
    assert "Output (LLM-assisted):" in mcp_call.dsl_snippet
    assert "Direct MCP call pattern (preferred when tool names are known" in mcp_call.dsl_snippet
    assert "use `mcp.call` directly with explicit `method` and `request`" in mcp_call.dsl_snippet
    assert "Output access patterns:" in mcp_call.dsl_snippet
    assert "data.steps.<id>.status" in mcp_call.dsl_snippet
    assert "data.steps.<id>.response" in mcp_call.dsl_snippet

    mcp_list = engine.registry.get("mcp.list")
    assert mcp_list is not None
    assert "can be passed directly into `mcp.call.input.tools` and/or `mcp.call.input.prompts`" in mcp_list.dsl_snippet
    assert "model: gpt-4o-mini" in mcp_list.dsl_snippet
    assert "prompt: \"Choose the right MCP capability and call it\"" in mcp_list.dsl_snippet

