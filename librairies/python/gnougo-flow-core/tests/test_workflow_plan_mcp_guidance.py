from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, McpServerMetadata, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine

_VALID_PLAN_YAML = """
version: 1
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
        long_schema_description = ("x" * 620) + " schema-tail-marker"
        return [
            McpToolInfo(
                name="list_repos",
                description="List repos",
                input_schema={
                    "type": "object",
                    "properties": {"user": {"type": "string", "description": long_schema_description}},
                    "required": ["user"],
                },
                output_schema={"type": "object", "properties": {"repositories": {"type": "array"}}, "additionalProperties": False},
                example_response={"repositories": [{"name": "demo"}]},
            )
        ]

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


class _SequencePrefilterLlm:
    def __init__(self) -> None:
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        if len(self.requests) == 1:
            return LLMResponse(
                text='{"servers":[{"name":"github","reason":"repository task"}]}',
                json_payload={"servers": [{"name": "github", "reason": "repository task"}]},
            )
        if len(self.requests) == 2:
            return LLMResponse(
                text='{"filtered":"## Server: github\\nTools (1):\\n- list_repos: List repos"}',
                json_payload={"filtered": "## Server: github\nTools (1):\n- list_repos: List repos"},
            )
        return LLMResponse(text=_VALID_PLAN_YAML)


class _TruncatingPrefilterLlm:
    def __init__(self) -> None:
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        if len(self.requests) == 1:
            return LLMResponse(
                text='{"servers":[{"name":"github","reason":"repository task"}]}',
                json_payload={"servers": [{"name": "github", "reason": "repository task"}]},
            )
        if len(self.requests) == 2:
            return LLMResponse(
                text='{"filtered":"## Server: github\\nTools (1):\\n- list_repos: List repos\\n  input_schema_json: {\\"type\\":\\"object\\"}…"}',
                json_payload={"filtered": '## Server: github\nTools (1):\n- list_repos: List repos\n  input_schema_json: {"type":"object"}…'},
            )
        return LLMResponse(text=_VALID_PLAN_YAML)


class _DryRunWithFilteredPromptLlm:
    def __init__(self) -> None:
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        if len(self.requests) == 1:
            return LLMResponse(
                text='{"servers":[{"name":"github","reason":"repository task"}]}',
                json_payload={"servers": [{"name": "github", "reason": "repository task"}]},
            )
        if len(self.requests) == 2:
            return LLMResponse(
                text='{"filtered":"## Server: github\\nTools (1):\\n- list_repos: List repos"}',
                json_payload={"filtered": "## Server: github\nTools (1):\n- list_repos: List repos"},
            )
        return LLMResponse(
            text="""
version: 1
name: generated-weather
skill:
  description: Generated weather workflow.
  tags: [generated]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: call_weather
        type: mcp.call
        input:
          server: weather
          kind: tool
          method: get_weather
          request:
            city: Paris
"""
        )


class _ForceWeatherServerPrefilterLlm:
    def __init__(self) -> None:
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        if len(self.requests) == 1:
            return LLMResponse(
                text='{"servers":[{"name":"github","reason":"repository task"}]}',
                json_payload={"servers": [{"name": "github", "reason": "repository task"}]},
            )
        if len(self.requests) == 2:
            filtered = "## Server: github\nTools (1):\n- list_repos: List repos\n\n## Server: weather\nTools (1):\n- get_weather: Get weather"
            return LLMResponse(
                text='{"filtered":"' + filtered.replace("\n", "\\n") + '"}',
                json_payload={"filtered": filtered},
            )
        return LLMResponse(text=_VALID_PLAN_YAML)


class _NamedToolSession:
    def __init__(self, server_name: str) -> None:
        self.server_name = server_name

    async def list_tools_async(self):
        if self.server_name == "github":
            return [McpToolInfo(name="list_repos", description="List repos")]
        return [McpToolInfo(name="get_weather", description="Get weather")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments):
        raise NotImplementedError

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class _TwoServerFactory:
    def __init__(self) -> None:
        self.server_metadata = [
            McpServerMetadata(name="github", description="GitHub repository automation and file operations"),
            McpServerMetadata(name="weather", description="Weather forecasts and city conditions"),
        ]
        self.opened_servers: list[str] = []

    async def get_client_async(self, server_name):
        self.opened_servers.append(server_name)
        return _NamedToolSession(server_name)


class _BrokenFactory:
    server_metadata = [McpServerMetadata(name="broken-server", description="A server that fails to connect")]

    async def get_client_async(self, server_name):
        raise RuntimeError("Connection refused")


def _compile_main(yaml_text: str):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


async def _run_plan(factory):
    source = """
    version: 1
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
    return next(prompt for prompt in llm.prompts if "<available_step_types>" in prompt)


@pytest.mark.asyncio
async def test_workflow_plan_prompt_mentions_llm_assisted_and_direct_mcp_call() -> None:
    prompt = await _run_plan(_ToolFactory())

    assert "use mcp.call with prompt + model (+ optional temperature)" in prompt
    assert "put the natural-language instruction in input.prompt" in prompt
    assert "Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly" in prompt
    assert "preserve JSON schema scalar types exactly" in prompt
    assert "numbers/integers/booleans must be unquoted YAML scalars" in prompt
    assert "prefer a YAML literal block (`|`) so nested quotes remain valid YAML" in prompt
    assert "list_repos" in prompt
    assert "input_schema_json:" in prompt
    assert "output_schema_json:" in prompt
    assert "example_response_json:" in prompt
    assert "```" not in prompt
    assert "<user_prompt>" in prompt
    assert "Build an MCP workflow" in prompt
    assert "</user_prompt>" in prompt
    assert "Instruction: Build an MCP workflow" not in prompt
    assert '"type": "string"' in prompt
    assert "schema-tail-marker" in prompt
    assert "schema-tail-marker…" not in prompt
    assert "repositories" in prompt
    assert "[STRUCTURED OUTPUT STRICT SCHEMAS]" not in prompt


@pytest.mark.asyncio
async def test_workflow_plan_server_prefilter_uses_descriptions_before_capability_discovery() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: gpt-4
                instruction: Build a workflow that lists GitHub repositories
              validate:
                compile: false
    """
    llm = _SequencePrefilterLlm()
    factory = _TwoServerFactory()
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = factory

    result = await engine.execute_async(_compile_main(source), {})

    assert result.success
    assert factory.opened_servers == ["github"]
    assert len(llm.requests) == 3
    assert llm.requests[0].structured_output_schema is not None
    assert llm.requests[0].structured_output_strict is True
    assert llm.requests[0].temperature is None
    assert "<server_catalog>" in llm.requests[0].prompt
    assert "</server_catalog>" in llm.requests[0].prompt
    assert "GitHub repository automation" in llm.requests[0].prompt
    assert "Weather forecasts" in llm.requests[0].prompt
    assert "list_repos" not in llm.requests[0].prompt
    assert "<user_prompt>" in llm.requests[0].prompt
    assert "Build a workflow that lists GitHub repositories" in llm.requests[0].prompt
    assert "</user_prompt>" in llm.requests[0].prompt
    assert "Instruction: Build a workflow that lists GitHub repositories" not in llm.requests[0].prompt
    assert llm.requests[1].structured_output_schema is not None
    assert llm.requests[1].temperature is None
    assert "<user_prompt>" in llm.requests[1].prompt
    assert "Build a workflow that lists GitHub repositories" in llm.requests[1].prompt
    assert "</user_prompt>" in llm.requests[1].prompt
    assert "Instruction: Build a workflow that lists GitHub repositories" not in llm.requests[1].prompt
    assert "list_repos" in llm.requests[2].prompt
    assert "get_weather" not in llm.requests[2].prompt


@pytest.mark.asyncio
async def test_workflow_plan_server_prefilter_uses_explicit_temperature_when_configured() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: gpt-4
                instruction: Build a workflow that lists GitHub repositories
                prefilter:
                  temperature: 1.0
              validate:
                compile: false
    """
    llm = _SequencePrefilterLlm()
    factory = _TwoServerFactory()
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = factory

    result = await engine.execute_async(_compile_main(source), {})

    assert result.success
    assert len(llm.requests) == 3
    assert llm.requests[0].temperature == 1.0
    assert llm.requests[1].temperature == 1.0
    assert llm.requests[2].temperature is None


@pytest.mark.asyncio
async def test_workflow_plan_capability_prefilter_falls_back_when_schema_is_truncated() -> None:
    source = """
    version: 1
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
    llm = _TruncatingPrefilterLlm()
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = _ToolFactory()

    result = await engine.execute_async(_compile_main(source), {})

    assert result.success
    assert len(llm.requests) == 3
    final_prompt = llm.requests[2].prompt
    assert "schema-tail-marker" in final_prompt
    assert "input_schema_json:" in final_prompt
    assert "```" not in final_prompt
    assert 'input_schema_json: {"type":"object"}…' not in final_prompt


@pytest.mark.asyncio
async def test_workflow_plan_dry_run_keeps_all_configured_mcp_servers_available() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: gpt-4
                instruction: Build a workflow that lists GitHub repositories
              validate:
                dry_run: true
    """
    llm = _DryRunWithFilteredPromptLlm()
    factory = _TwoServerFactory()
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = factory

    result = await engine.execute_async(_compile_main(source), {})

    assert result.success
    assert "get_weather" not in llm.requests[2].prompt
    assert "weather" in factory.opened_servers


@pytest.mark.asyncio
async def test_workflow_plan_server_prefilter_force_includes_servers_referenced_by_context() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: gpt-4
                instruction: Build a workflow from the existing MCP call.
                context: |
                  - id: existing
                    type: mcp.call
                    input:
                      server: weather
                      method: get_weather
              validate:
                compile: false
    """
    llm = _ForceWeatherServerPrefilterLlm()
    factory = _TwoServerFactory()
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = factory

    result = await engine.execute_async(_compile_main(source), {})

    assert result.success
    assert factory.opened_servers == ["github", "weather"]
    assert "get_weather" in llm.requests[2].prompt


@pytest.mark.asyncio
async def test_workflow_plan_prompt_falls_back_when_mcp_discovery_fails() -> None:
    prompt = await _run_plan(_BrokenFactory())

    assert "<available_mcp_servers>" in prompt
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

    llm_call = engine.registry.get("llm.call")
    assert llm_call is not None
    assert "Structured output:" in llm_call.dsl_snippet
    assert "structured_output:" in llm_call.dsl_snippet
    assert "schema_inline:" in llm_call.dsl_snippet
    assert "strict: true" in llm_call.dsl_snippet
    assert "Every schema object with `properties` MUST have `required` listing EVERY key from `properties`" in llm_call.dsl_snippet
    assert "Optional fields must still be listed in `required`" in llm_call.dsl_snippet
    assert "additionalProperties: false" in llm_call.dsl_snippet
    assert "anyOf:" in llm_call.dsl_snippet
    assert "structured_output.schema_ref" in llm_call.dsl_snippet
