import pytest
import yaml

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, LLMToolCall, McpCallResult, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _ensure_generated_skill(yaml_text: str) -> str:
    try:
        parsed = yaml.safe_load(yaml_text)
    except Exception:
        return yaml_text

    if not isinstance(parsed, dict) or isinstance(parsed.get("skill"), dict):
        return yaml_text

    parsed["skill"] = {
        "description": "Generated workflow.",
        "tags": ["generated"],
        "inputs": {},
        "outputs": {},
    }
    return yaml.safe_dump(parsed, sort_keys=False, allow_unicode=False)


class FakeLLMClient:
    async def call_async(self, request):
        if "Generate a valid GnOuGo.Flow YAML document" in request.prompt:
            return LLMResponse(
                text=_ensure_generated_skill("""
                version: 1
                workflows:
                  main:
                    steps:
                      - id: built
                        type: set
                        input:
                          ok: true
                """)
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


class ErrorMcpSession(FakeMcpSession):
    def __init__(self, *, is_error: bool, content):
        self._is_error = is_error
        self._content = content

    async def call_tool_async(self, tool_name, arguments):
        return McpCallResult(is_error=self._is_error, content=self._content)


class ErrorMcpFactory:
    def __init__(self, session):
        self.session = session
        self.server_metadata = []

    async def get_client_async(self, server_name):
        return self.session


@pytest.mark.asyncio
async def test_runtime_llm_mcp_and_plan_execute() -> None:
    yaml_text = """
    version: 1
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
async def test_mcp_call_raise_on_error_triggers_on_error_with_mcp_details() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: call
            type: mcp.call
            input:
              server: demo
              method: search
              request: {}
              raise_on_error: true
            on_error:
              cases:
                - if: '${error.code == "MCP_CALL_ERROR"}'
                  action: continue
                  set_output:
                    recovered: true
                    code: "${error.code}"
                    mcp_code: "${error.details.mcp_error_code}"
                    mcp_message: "${error.details.mcp_error_message}"
        outputs:
          recovered: "${data.steps.call.recovered}"
          code: "${data.steps.call.code}"
          mcp_code: "${data.steps.call.mcp_code}"
          mcp_message: "${data.steps.call.mcp_message}"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()
    engine.mcp_client_factory = ErrorMcpFactory(
        ErrorMcpSession(
            is_error=True,
            content={"error_code": "NOT_FOUND", "error_message": "Thing was not found."},
        )
    )

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert result.outputs["recovered"] is True
    assert result.outputs["code"] == "MCP_CALL_ERROR"
    assert result.outputs["mcp_code"] == "NOT_FOUND"
    assert result.outputs["mcp_message"] == "Thing was not found."


@pytest.mark.asyncio
async def test_mcp_call_raise_on_error_detects_structured_failure_envelope() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: call
            type: mcp.call
            input:
              server: demo
              method: search
              request: {}
              raise_on_error: true
            on_error:
              cases:
                - action: continue
                  set_output:
                    recovered: true
                    mcp_code: "${error.details.mcp_error_code}"
        outputs:
          recovered: "${data.steps.call.recovered}"
          mcp_code: "${data.steps.call.mcp_code}"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()
    engine.mcp_client_factory = ErrorMcpFactory(
        ErrorMcpSession(
            is_error=False,
            content={"success": False, "error_code": "ALREADY_EXISTS", "error_message": "Already exists."},
        )
    )

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert result.outputs["recovered"] is True
    assert result.outputs["mcp_code"] == "ALREADY_EXISTS"


@pytest.mark.asyncio
async def test_runtime_execute_supports_to_json_alias_in_generated_workflow_outputs() -> None:
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
                instruction: "make a simple workflow"
          - id: execute
            type: workflow.execute
            input:
              from_step: plan
        outputs:
          planned_json: "${data.steps.execute.outputs.built_json}"
    """

    generated_yaml = """
    version: 1
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
        self._yaml_text = _ensure_generated_skill(yaml_text)

    async def call_async(self, request):
        return LLMResponse(text=self._yaml_text)
