import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, McpServerMetadata, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.runtime_steps import SequenceExecutor


class CapturePlanLlm:
    def __init__(self, yaml_text: str) -> None:
        self.yaml_text = yaml_text
        self.prompts: list[str] = []

    async def call_async(self, request):
        self.prompts.append(request.prompt)
        return LLMResponse(text=self.yaml_text)


class FakeMcpSession:
    server_name = "demo"

    async def list_tools_async(self):
        return [McpToolInfo(name="search", description="search docs")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments):
        raise NotImplementedError

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class FakeMcpFactory:
    def __init__(self):
        self.server_metadata = [McpServerMetadata(name="demo", description="demo server")]

    async def get_client_async(self, server_name):
        return FakeMcpSession()


@pytest.mark.asyncio
async def test_workflow_plan_prompt_contains_dotnet_like_sections() -> None:
    source = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build something"
    """
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: done
            type: set
            input:
              text: ok
    """

    llm = CapturePlanLlm(generated_yaml)
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = FakeMcpFactory()

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert llm.prompts
    prompt = next((p for p in llm.prompts if "[AVAILABLE STEP TYPES]" in p), llm.prompts[-1])
    assert "[AVAILABLE STEP TYPES]" in prompt
    assert "[AVAILABLE MCP SERVERS]" in prompt
    assert "[STEP EXCEPTIONS BY TYPE]" in prompt
    assert "## Server: demo" in prompt


@pytest.mark.asyncio
async def test_workflow_plan_policy_blocks_remote_refs() -> None:
    source = """
    dsl: 1
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
                allow_remote_workflow_refs: false
    """
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: call_remote
            type: workflow.call
            input:
              ref:
                kind: url
                url: https://example.com/wf.yaml
              args: {}
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "WORKFLOW_FETCH_POLICY"
    assert "allow_remote_workflow_refs" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_policy_enforces_denied_types_on_nested_steps() -> None:
    source = """
    dsl: 1
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
                allowed_step_types: [sequence, workflow.plan]
                denied_step_types: [workflow.plan]
              validate:
                compile: false
    """
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: wrapper
            type: sequence
            steps:
              - id: nested_plan
                type: workflow.plan
                input:
                  generator:
                    instruction: nested
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_POLICY"
    assert "workflow.plan" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_policy_enforces_allowed_types_on_nested_steps() -> None:
    source = """
    dsl: 1
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
                allowed_step_types: [sequence]
              validate:
                compile: false
    """
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: wrapper
            type: sequence
            steps:
              - id: nested_set
                type: set
                input:
                  text: ok
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_POLICY"
    assert "set" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_policy_blocks_nested_remote_refs() -> None:
    source = """
    dsl: 1
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
                allow_remote_workflow_refs: false
              validate:
                compile: false
    """
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: wrapper
            type: sequence
            steps:
              - id: call_remote
                type: workflow.call
                input:
                  ref:
                    kind: url
                    url: https://example.com/wf.yaml
                  args: {}
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "WORKFLOW_FETCH_POLICY"
    assert "allow_remote_workflow_refs" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_limits_count_nested_steps() -> None:
    source = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build something"
              limits:
                max_steps_total: 1
              validate:
                compile: false
    """
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: wrapper
            type: sequence
            steps:
              - id: nested_set
                type: set
                input:
                  text: ok
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_POLICY"
    assert "max_steps_total" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_normalizes_workflow_body_without_workflows_root() -> None:
    source = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build something"
          - id: run
            type: workflow.execute
            input:
              from_step: plan
        outputs:
          answer: "${data.steps.run.outputs.answer}"
    """
    generated_yaml = """
    dsl: 1
    steps:
      - id: done
        type: set
        input:
          answer: "normalized"
    outputs:
      answer: "${data.steps.done.answer}"
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert result.outputs["answer"] == "normalized"


@pytest.mark.asyncio
async def test_workflow_plan_uses_prompt_snippet_from_executor_class() -> None:
    source = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build something"
    """
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: done
            type: set
            input:
              text: ok
    """

    llm = CapturePlanLlm(generated_yaml)
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = FakeMcpFactory()

    old = getattr(SequenceExecutor, "dsl_snippet", None)
    SequenceExecutor.dsl_snippet = "### sequence test\nSequence test snippet from class"
    try:
        compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))
        result = await engine.execute_async(compiled.workflows["main"], {})
        assert result.success is True
        prompt = next((p for p in llm.prompts if "[AVAILABLE STEP TYPES]" in p), "")
        assert "Sequence test snippet from class" in prompt
    finally:
        SequenceExecutor.dsl_snippet = old


