from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import ErrorCodes, WorkflowRuntimeException
from gnougo_flow_core.models import LLMResponse
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


class _PlanLlm:
    def __init__(self, *responses) -> None:
        self._responses = list(responses)
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        if request.prompt.startswith("Select only MCP servers/capabilities relevant to the task."):
            return LLMResponse(json={"filtered": "No MCP servers configured."})
        response = self._responses.pop(0)
        if isinstance(response, Exception):
            raise response
        return LLMResponse(text=response)


class _Telemetry:
    def __init__(self) -> None:
        self.workflow_starts = []
        self.workflow_ends = []

    def workflow_start(self, info):
        self.workflow_starts.append(info)
        return object()

    def workflow_end(self, span, info):
        self.workflow_ends.append(info)

    def step_start(self, parent, info):
        return object()

    def step_end(self, span, info):
        return None


def _compile_main(yaml_text: str):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


async def _run_main(yaml_text: str, inputs=None, llm_client=None, engine: WorkflowEngine | None = None):
    engine = engine or WorkflowEngine()
    engine.llm_client = llm_client
    return await engine.execute_async(_compile_main(yaml_text), inputs or {})


@pytest.mark.asyncio
async def test_workflow_execute_basic_plan_then_execute_returns_output() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: greet
            type: template.render
            input:
              engine: mustache
              template: "Hello, World!"
              mode: text
        outputs:
          answer:
            expr: "${data.steps.greet.text}"
            type: string
    """

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: Generate a greeting
                  validate:
                    compile: true
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
            outputs:
              answer: "${data.steps.run.outputs.answer}"
        """,
        llm_client=_PlanLlm(generated_yaml),
    )

    assert result.success
    assert result.outputs["answer"] == "Hello, World!"


@pytest.mark.asyncio
async def test_workflow_execute_missing_from_step_fails_with_input_validation() -> None:
    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: run
                type: workflow.execute
                input: {}
        """
    )

    assert not result.success
    assert result.error.code == ErrorCodes.INPUT_VALIDATION


@pytest.mark.asyncio
async def test_workflow_execute_nonexistent_plan_step_mentions_step_name() -> None:
    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: run
                type: workflow.execute
                input:
                  from_step: does_not_exist
        """
    )

    assert not result.success
    assert result.error.code == ErrorCodes.INPUT_VALIDATION
    assert "does_not_exist" in result.error.message


@pytest.mark.asyncio
async def test_workflow_execute_plan_step_missing_yaml_mentions_yaml() -> None:
    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: fake_plan
                type: set
                input:
                  workflow:
                    name: fake
              - id: run
                type: workflow.execute
                input:
                  from_step: fake_plan
        """
    )

    assert not result.success
    assert result.error.code == ErrorCodes.INPUT_VALIDATION
    assert "YAML" in result.error.message


@pytest.mark.asyncio
async def test_workflow_execute_multi_step_generated_workflow_executes_all_steps() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: step1
            type: set
            input:
              value: first
          - id: step2
            type: template.render
            input:
              engine: mustache
              template: "{{prefix}}-second"
              data:
                prefix: "${data.steps.step1.value}"
              mode: text
        outputs:
          result:
            expr: "${data.steps.step2.text}"
            type: string
    """

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: test
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
            outputs:
              result: "${data.steps.run.outputs.result}"
              steps_executed: "${data.steps.run.run.steps_executed}"
              success: "${data.steps.run.run.success}"
        """,
        llm_client=_PlanLlm(generated_yaml),
    )

    assert result.success
    assert result.outputs["result"] == "first-second"
    assert result.outputs["steps_executed"] == 2
    assert result.outputs["success"] is True


@pytest.mark.asyncio
async def test_workflow_execute_no_outputs_defined_falls_back_to_steps_data() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: calc
            type: set
            input:
              answer: 42
    """

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: test
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
        """,
        llm_client=_PlanLlm(generated_yaml),
    )

    assert result.success
    assert result.step_results[-1].output["outputs"]["calc"]["answer"] == 42


@pytest.mark.asyncio
async def test_workflow_execute_with_args_defaults_and_type_validation() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        inputs:
          name:
            type: string
            required: true
          suffix:
            type: string
            default: "!"
        steps:
          - id: greet
            type: template.render
            input:
              engine: mustache
              template: "Hello, {{name}}{{suffix}}"
              data:
                name: "${data.inputs.name}"
                suffix: "${data.inputs.suffix}"
              mode: text
        outputs:
          greeting:
            expr: "${data.steps.greet.text}"
            type: string
    """

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: test
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
                  args:
                    name: Alice
            outputs:
              greeting: "${data.steps.run.outputs.greeting}"
        """,
        llm_client=_PlanLlm(generated_yaml),
    )

    assert result.success
    assert result.outputs["greeting"] == "Hello, Alice!"


@pytest.mark.asyncio
async def test_workflow_execute_invalid_args_fail_with_input_validation() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        inputs:
          name:
            type: string
            required: true
        steps:
          - id: s
            type: set
            input:
              value: "${data.inputs.name}"
    """

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: test
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
                  args:
                    name: 123
        """,
        llm_client=_PlanLlm(generated_yaml),
    )

    assert not result.success
    assert result.error.code == ErrorCodes.INPUT_VALIDATION
    assert "expected string" in result.error.message


@pytest.mark.asyncio
async def test_workflow_execute_exceeds_call_depth_fails_with_cycle_detected() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      gen:
        steps:
          - id: s
            type: set
            input:
              ok: true
    """
    engine = WorkflowEngine()
    engine.limits.max_call_depth = 0

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: test
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
        """,
        llm_client=_PlanLlm(generated_yaml),
        engine=engine,
    )

    assert not result.success
    assert result.error.code == ErrorCodes.WORKFLOW_CYCLE_DETECTED


@pytest.mark.asyncio
async def test_workflow_execute_generated_workflow_failure_propagates_error() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: call
            type: llm.call
            input:
              model: gpt-4
              prompt: Hello
    """

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: test
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
        """,
        llm_client=_PlanLlm(generated_yaml, WorkflowRuntimeException(ErrorCodes.LLM_NETWORK, "LLM unreachable")),
    )

    assert not result.success
    assert result.error.code == ErrorCodes.LLM_NETWORK


@pytest.mark.asyncio
async def test_workflow_execute_invalid_yaml_fails_gracefully() -> None:
    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: fake_plan
                type: set
                input:
                  yaml: "this is not valid yaml: [[[["
              - id: run
                type: workflow.execute
                input:
                  from_step: fake_plan
        """
    )

    assert not result.success
    assert result.error.code == ErrorCodes.INPUT_VALIDATION
    assert "Invalid planned workflow YAML" in result.error.message


@pytest.mark.asyncio
async def test_workflow_execute_starts_dedicated_subworkflow_telemetry_span() -> None:
    generated_yaml = """
    dsl: 1
    workflows:
      generated:
        steps:
          - id: s
            type: set
            input:
              ok: true
    """
    engine = WorkflowEngine()
    telemetry = _Telemetry()
    engine.telemetry = telemetry

    result = await _run_main(
        """
        dsl: 1
        workflows:
          main:
            steps:
              - id: generate
                type: workflow.plan
                input:
                  generator:
                    model: gpt-4
                    instruction: test
              - id: run
                type: workflow.execute
                input:
                  from_step: generate
        """,
        llm_client=_PlanLlm(generated_yaml),
        engine=engine,
    )

    assert result.success
    assert [info["workflow_name"] for info in telemetry.workflow_starts] == ["main", "generated"]
    assert len(telemetry.workflow_ends) == 2


