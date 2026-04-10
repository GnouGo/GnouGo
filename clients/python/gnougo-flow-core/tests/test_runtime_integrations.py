import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, LLMToolCall, McpCallResult, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


class FakeLLMClient:
    async def call_async(self, request):
        if "Generate a valid GnOuGo.Flow YAML document" in request.prompt:
            return LLMResponse(
                text="""
                dsl: 1
                workflows:
                  main:
                    steps:
                      - id: built
                        type: set
                        input:
                          ok: true
                """
            )
        if request.tools:
            return LLMResponse(
                text="I will call one tool",
                tool_calls=[LLMToolCall(id="1", name=request.tools[0].name, arguments={"q": "x"})],
            )
        return LLMResponse(text="hello", json_payload={"ok": True}, usage={"total_tokens": 7})


class FakeMcpSession:
    server_name = "demo"

    async def list_tools_async(self):
        return [McpToolInfo(name="search", description="search tool")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments):
        return McpCallResult(is_error=False, content={"tool": tool_name, "args": arguments})

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class FakeMcpFactory:
    def __init__(self):
        self.server_metadata = []

    async def get_client_async(self, server_name):
        return FakeMcpSession()


@pytest.mark.asyncio
async def test_runtime_llm_mcp_and_plan_execute() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: ask
            type: llm.call
            input:
              model: fake
              prompt: "hello"
          - id: list
            type: mcp.list
            input:
              servers: [demo]
              include: [tools]
          - id: call
            type: mcp.call
            input:
              server: demo
              method: search
              request:
                q: "value"
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "make a simple workflow"
          - id: execute
            type: workflow.execute
            input:
              from_step: plan
        outputs:
          llm_json: "${data.steps.ask.json.ok}"
          mcp_tool: "${data.steps.call.response.tool}"
          planned_ok: "${data.steps.execute.outputs.built.ok}"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()
    engine.llm_client = FakeLLMClient()
    engine.mcp_client_factory = FakeMcpFactory()

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert result.outputs["llm_json"] is True
    assert result.outputs["mcp_tool"] == "search"
    assert result.outputs["planned_ok"] is True


@pytest.mark.asyncio
async def test_runtime_execute_supports_to_json_alias_in_generated_workflow_outputs() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "make a simple workflow"
          - id: execute
            type: workflow.execute
            input:
              from_step: plan
        outputs:
          planned_json: "${data.steps.execute.outputs.built_json}"
    """

    generated_yaml = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: built
            type: set
            input:
              ok: true
              count: 2
        outputs:
          built_json: "${toJson(data.steps.built)}"
    """

    engine = WorkflowEngine()
    engine.llm_client = CaptureFixedPlanLlm(generated_yaml)
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert '"ok": true' in result.outputs["planned_json"]


class CaptureFixedPlanLlm:
    def __init__(self, yaml_text: str) -> None:
        self._yaml_text = yaml_text

    async def call_async(self, request):
        return LLMResponse(text=self._yaml_text)


