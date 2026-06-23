import pytest
import yaml

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, McpServerMetadata, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.runtime_steps import SequenceExecutor


def ensure_generated_skill(yaml_text: str) -> str:
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


class CapturePlanLlm:
    def __init__(self, yaml_text: str) -> None:
        self.yaml_text = ensure_generated_skill(yaml_text)
        self.prompts: list[str] = []

    async def call_async(self, request):
        self.prompts.append(request.prompt)
        return LLMResponse(text=self.yaml_text)


class SequencePlanLlm:
    def __init__(self, responses: list[str]) -> None:
        self.responses = [ensure_generated_skill(response) for response in responses]
        self.prompts: list[str] = []

    async def call_async(self, request):
        self.prompts.append(request.prompt)
        index = min(len(self.prompts), len(self.responses)) - 1
        return LLMResponse(text=self.responses[index])


class PipelinePlanLlm:
    def __init__(self, leaf_yaml: str, assembly_yaml: str, annotated_markdown: str | None = None) -> None:
        self.leaf_yaml = ensure_generated_skill(leaf_yaml)
        self.assembly_yaml = assembly_yaml
        self.annotated_markdown = annotated_markdown or """
        # Resolve issue

        :::subworkflow name="parse_issue"
        goal: Parse an issue key.
        inputs:
          issue_key: string
        outputs:
          issue_number: integer
        extract_reason: Reusable issue parsing logic.
        content:
          Parse the issue key and return its numeric issue_number.
        :::

        ## Main workflow orchestration

        Call parse_issue and expose its issue_number.
        """
        self.prompts: list[str] = []

    async def call_async(self, request):
        self.prompts.append(request.prompt)
        prompt = request.prompt
        if "preparing a raw user automation prompt" in prompt:
            return LLMResponse(text="# Resolve issue\n\nParse the issue and expose the number.")
        if "annotate normalized automation Markdown" in prompt:
            return LLMResponse(text=self.annotated_markdown)
        if "Generate exactly one leaf GnOuGo workflow named `parse_issue`" in prompt:
            return LLMResponse(text=self.leaf_yaml)
        if "assembling the parent `main` workflow" in prompt:
            return LLMResponse(text=self.assembly_yaml)
        return LLMResponse(text=self.leaf_yaml)


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


class DocsMcpSession:
    server_name = "docs"

    def __init__(self, tool: McpToolInfo) -> None:
        self.tool = tool

    async def list_tools_async(self):
        return [self.tool]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments):
        raise NotImplementedError

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class DocsMcpFactory:
    def __init__(self, tool: McpToolInfo) -> None:
        self.tool = tool
        self.server_metadata = [McpServerMetadata(name="docs", description="Documentation tools")]

    async def get_client_async(self, server_name):
        return DocsMcpSession(self.tool)


class FlakyDocsMcpFactory(DocsMcpFactory):
    def __init__(self, tool: McpToolInfo) -> None:
        super().__init__(tool)
        self.calls = 0

    async def get_client_async(self, server_name):
        self.calls += 1
        if self.calls < 3:
            raise RuntimeError(f"transient discovery failure {self.calls}")
        return DocsMcpSession(self.tool)


@pytest.mark.asyncio
async def test_workflow_plan_prompt_contains_dotnet_like_sections() -> None:
    source = """
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
    """
    generated_yaml = """
    version: 1
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
    prompt = next((p for p in llm.prompts if "<available_step_types>" in p), llm.prompts[-1])
    assert "<available_step_types>" in prompt
    assert "</available_step_types>" in prompt
    assert "[AVAILABLE STEP TYPES]" not in prompt
    assert "<available_mcp_servers>" in prompt
    assert "</available_mcp_servers>" in prompt
    assert "<step_exceptions_by_type>" in prompt
    assert "</step_exceptions_by_type>" in prompt
    assert "## Server: demo" in prompt
    assert "Function arguments are evaluated before the function runs" in prompt
    assert "coalesce(data.steps.branch_a.value, data.steps.branch_b.value)" in prompt
    assert "produced only inside `switch` cases" in prompt
    assert "version, name, skill, workflows" in prompt
    assert "- skill: required object" in prompt
    assert "1. Inspect every MCP tool used by this workflow." in prompt
    assert "Never satisfy a missing required MCP argument with data.env.*, empty string, fake values, or casts." in prompt
    assert "Prefer the exact MCP argument name and type." in prompt


@pytest.mark.asyncio
async def test_workflow_plan_policy_blocks_remote_refs() -> None:
    source = """
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
                allow_remote_workflow_refs: false
    """
    generated_yaml = """
    version: 1
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
                allowed_step_types: [sequence, workflow.plan]
                denied_step_types: [workflow.plan]
              validate:
                compile: false
    """
    generated_yaml = """
    version: 1
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
                allowed_step_types: [sequence]
              validate:
                compile: false
    """
    generated_yaml = """
    version: 1
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
                allow_remote_workflow_refs: false
              validate:
                compile: false
    """
    generated_yaml = """
    version: 1
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
              limits:
                max_steps_total: 1
              validate:
                compile: false
    """
    generated_yaml = """
    version: 1
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
          - id: run
            type: workflow.execute
            input:
              from_step: plan
        outputs:
          answer: "${data.steps.run.outputs.answer}"
    """
    generated_yaml = """
    version: 1
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
    """
    generated_yaml = """
    version: 1
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
        prompt = next((p for p in llm.prompts if "<available_step_types>" in p), "")
        assert "Sequence test snippet from class" in prompt
    finally:
        SequenceExecutor.dsl_snippet = old


@pytest.mark.asyncio
async def test_workflow_plan_reprompts_on_validator_diagnostics_not_only_compiler_errors() -> None:
    source = """
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
                prefilter: false
              on_invalid:
                action: reprompt
                max_attempts: 2
    """
    invalid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: s
            type: definitely.not.a.step
    """
    valid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: s
            type: set
            input:
              text: ok
    """

    llm = SequencePlanLlm([invalid_yaml, valid_yaml])
    engine = WorkflowEngine()
    engine.llm_client = llm
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(source))

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert len(llm.prompts) == 2
    assert "<previous_error>" in llm.prompts[1]
    assert "<invalid_yaml>" in llm.prompts[1]
    assert "<user_prompt>" in llm.prompts[1]
    assert "build something" in llm.prompts[1]
    assert "</user_prompt>" in llm.prompts[1]
    assert "Instruction: build something" not in llm.prompts[1]
    assert "<previous_error>" in llm.prompts[1]
    assert "</previous_error>" in llm.prompts[1]
    assert "<invalid_yaml>" in llm.prompts[1]
    assert "</invalid_yaml>" in llm.prompts[1]
    assert "<dsl_reference>" not in llm.prompts[1]
    assert "<step_exceptions_by_type>" not in llm.prompts[1]
    assert "STEP_TYPE_UNKNOWN" in llm.prompts[1]


@pytest.mark.asyncio
async def test_workflow_plan_validation_returns_structural_and_semantic_diagnostics_together() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build a workflow with typed inputs and outputs"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        inputs:
          bad_input:
            type: string
            properties:
              nested:
                type: string
        steps:
          - id: collect
            type: set
            input:
              value: ok
        outputs:
          bad_output:
            type: string
            properties:
              nested:
                type: string
            expr: "${data.steps.missing.value}"
    """
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "INVALID_INPUT_SCHEMA" in result.error.message
    assert "INVALID_OUTPUT_SCHEMA" in result.error.message
    assert "STEP_REFERENCE_UNKNOWN" in result.error.message
    assert "data.steps.missing.value" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_allows_known_mcp_response_property() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
          - id: map
            type: set
            input:
              title: "${data.steps.fetch.response.title}"
    """
    tool = McpToolInfo(
        name="get_doc",
        description="Get a document",
        output_schema={"type": "object", "properties": {"title": {"type": "string"}}, "additionalProperties": False},
    )
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_unknown_mcp_response_property() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
          - id: map
            type: set
            input:
              title: "${data.steps.fetch.response.missing_title}"
    """
    tool = McpToolInfo(
        name="get_doc",
        description="Get a document",
        output_schema={"type": "object", "properties": {"title": {"type": "string"}}, "additionalProperties": False},
    )
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "STEP_OUTPUT_PROPERTY_UNKNOWN" in result.error.message
    assert "data.steps.fetch.response.missing_title" in result.error.message
    assert "data.steps.fetch.response.title" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_unknown_mcp_server() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: missing_docs
              method: get_doc
              request:
                id: intro
    """
    tool = McpToolInfo(name="get_doc", description="Get a document")
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "MCP_SERVER_UNKNOWN" in result.error.message
    assert "missing_docs" in result.error.message
    assert "mcp.server:docs" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_unknown_mcp_method() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: missing_doc
              request:
                id: intro
    """
    tool = McpToolInfo(name="get_doc", description="Get a document")
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "MCP_METHOD_UNKNOWN" in result.error.message
    assert "missing_doc" in result.error.message
    assert "mcp.server:docs.method:get_doc" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_deep_access_into_opaque_mcp_response() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
          - id: map
            type: set
            input:
              title: "${data.steps.fetch.response.title}"
    """
    tool = McpToolInfo(name="get_doc", description="Get a document without declared output contract")
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "OPAQUE_RESPONSE_DEEP_ACCESS" in result.error.message
    assert "json(data.steps.fetch.response)" in result.error.message
    assert "structured_output" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_switch_branch_step_output_mapping_after_switch() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build branching workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: classify
            type: set
            input:
              classification: question
          - id: route_action
            type: switch
            cases:
              - when: "${data.steps.classify.classification == 'question'}"
                steps:
                  - id: set_question_result
                    type: set
                    input:
                      pr_link: "N/A"
              - when: "${data.steps.classify.classification == 'bug'}"
                steps:
                  - id: set_fix_result
                    type: set
                    input:
                      pr_link: "https://example.test/pr/1"
            default:
              - id: set_complex_result
                type: set
                input:
                  pr_link: "N/A"
          - id: map_result
            type: set
            input:
              pr_link: "${coalesce(data.steps.set_fix_result.pr_link, data.steps.set_question_result.pr_link, data.steps.set_complex_result.pr_link, 'N/A')}"
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "STEP_REFERENCE_NOT_AVAILABLE" in result.error.message
    assert "data.steps.set_fix_result.pr_link" in result.error.message
    assert "data.steps.set_question_result.pr_link" in result.error.message
    assert "data.steps.set_complex_result.pr_link" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_reprompt_can_fix_switch_branch_mapping() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build branching workflow"
                prefilter: false
              on_invalid:
                action: reprompt
                max_attempts: 2
    """
    invalid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: classify
            type: set
            input:
              classification: question
          - id: route_action
            type: switch
            cases:
              - when: "${data.steps.classify.classification == 'question'}"
                steps:
                  - id: set_question_result
                    type: set
                    input:
                      pr_link: "N/A"
              - when: "${data.steps.classify.classification == 'bug'}"
                steps:
                  - id: set_fix_result
                    type: set
                    input:
                      pr_link: "https://example.test/pr/1"
            default:
              - id: set_complex_result
                type: set
                input:
                  pr_link: "N/A"
          - id: map_result
            type: set
            input:
              pr_link: "${coalesce(data.steps.set_fix_result.pr_link, data.steps.set_question_result.pr_link, data.steps.set_complex_result.pr_link, 'N/A')}"
    """
    fixed_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: classify
            type: set
            input:
              classification: question
          - id: route_action
            type: switch
            cases:
              - when: "${data.steps.classify.classification == 'question'}"
                steps:
                  - id: set_question_result
                    type: set
                    output: branch_result
                    input:
                      pr_link: "N/A"
              - when: "${data.steps.classify.classification == 'bug'}"
                steps:
                  - id: set_fix_result
                    type: set
                    output: branch_result
                    input:
                      pr_link: "https://example.test/pr/1"
            default:
              - id: set_complex_result
                type: set
                output: branch_result
                input:
                  pr_link: "N/A"
          - id: map_result
            type: set
            input:
              pr_link: "${data.branch_result.pr_link}"
    """

    llm = SequencePlanLlm([invalid_yaml, fixed_yaml])
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    assert len(llm.prompts) == 2
    assert "SEMANTIC_MAPPING_ERROR" in llm.prompts[1]
    assert "STEP_REFERENCE_NOT_AVAILABLE" in llm.prompts[1]
    assert "set_fix_result" in llm.prompts[1]


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_reprompt_can_fix_opaque_mcp_response_mapping() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
              on_invalid:
                action: reprompt
                max_attempts: 2
    """
    invalid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
          - id: map
            type: set
            input:
              title: "${data.steps.fetch.response.title}"
    """
    fixed_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
          - id: normalize
            type: llm.call
            input:
              model: gpt-4o-mini
              prompt: "Normalize this MCP response: ${json(data.steps.fetch.response)}"
              structured_output:
                schema_inline:
                  type: object
                  properties:
                    title: { type: string }
                  required: [title]
                  additionalProperties: false
          - id: map
            type: set
            input:
              title: "${data.steps.normalize.json.title}"
    """
    tool = McpToolInfo(name="get_doc", description="Get a document without declared output contract")
    llm = SequencePlanLlm([invalid_yaml, fixed_yaml])
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    assert len(llm.prompts) == 2
    assert "OPAQUE_RESPONSE_DEEP_ACCESS" in llm.prompts[1]
    assert "structured_output" in llm.prompts[1]


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_invalid_mcp_request_against_input_schema() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "close a github issue"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: close_issue
            type: mcp.call
            input:
              server: docs
              method: issue_write
              request:
                owner: AxaFrance
                repo: oidc-client
                issue_number: 1651
                state: closed
    """
    tool = McpToolInfo(
        name="issue_write",
        description="Update an issue",
        input_schema={
            "type": "object",
            "required": ["owner", "repo", "issue_number", "method"],
            "properties": {
                "owner": {"type": "string"},
                "repo": {"type": "string"},
                "issue_number": {"type": "number"},
                "method": {"type": "string"},
                "state": {"type": "string"},
            },
            "additionalProperties": False,
        },
    )
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "MCP_REQUEST_SCHEMA_INVALID" in result.error.message
    assert "input.request.method" in result.error.message
    assert "docs/issue_write" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_coerces_string_scalars_for_mcp_request_schema() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "list github pull requests"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: list_prs
            type: mcp.call
            input:
              server: docs
              method: list_pull_requests
              request:
                owner: AxaFrance
                repo: oidc-client
                perPage: "100"
    """
    tool = McpToolInfo(
        name="list_pull_requests",
        description="List pull requests",
        input_schema={
            "type": "object",
            "required": ["owner", "repo"],
            "properties": {
                "owner": {"type": "string"},
                "repo": {"type": "string"},
                "perPage": {"type": "number"},
            },
            "additionalProperties": False,
        },
    )
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    yaml_text = result.outputs["plan"]["yaml"]
    assert "perPage: 100" in yaml_text
    assert 'perPage: "100"' not in yaml_text


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_coerces_nested_mcp_request_scalars_like_dotnet() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "normalize mcp request"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: call_tool
            type: mcp.call
            input:
              server: docs
              method: normalize_request
              request:
                count: "5"
                enabled: "true"
                nested:
                  threshold: "0.75"
                flags: ["false", "true"]
                metadata:
                  retries: "2"
                payload:
                  value: "42"
    """
    tool = McpToolInfo(
        name="normalize_request",
        description="Normalize request scalar types",
        input_schema={
            "type": "object",
            "required": ["count", "enabled", "nested", "flags", "metadata", "payload"],
            "properties": {
                "count": {"type": "integer"},
                "enabled": {"type": "boolean"},
                "nested": {
                    "type": "object",
                    "required": ["threshold"],
                    "properties": {"threshold": {"type": "number"}},
                    "additionalProperties": False,
                },
                "flags": {"type": "array", "items": {"type": "boolean"}},
                "metadata": {"type": "object", "additionalProperties": {"type": "integer"}},
                "payload": {
                    "oneOf": [
                        {
                            "type": "object",
                            "required": ["value"],
                            "properties": {"value": {"type": "integer"}},
                            "additionalProperties": False,
                        },
                        {"type": "null"},
                    ]
                },
            },
            "additionalProperties": False,
        },
    )
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    request = yaml.safe_load(result.outputs["plan"]["yaml"])["workflows"]["main"]["steps"][0]["input"]["request"]
    assert request["count"] == 5
    assert request["enabled"] is True
    assert request["nested"]["threshold"] == 0.75
    assert request["flags"] == [False, True]
    assert request["metadata"]["retries"] == 2
    assert request["payload"]["value"] == 42


@pytest.mark.asyncio
async def test_workflow_plan_dry_run_rejects_freeform_llm_text_used_as_number() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build token workflow"
                prefilter: false
              validate:
                dry_run: true
              on_invalid:
                action: stop
                max_attempts: 1
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: extract
            type: llm.call
            input:
              prompt: "Return a token budget"
          - id: answer
            type: llm.call
            input:
              prompt: "Answer briefly"
              max_tokens: "${data.steps.extract.text}"
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "dry_run" in result.error.message
    assert "Expected number" in result.error.message
    assert "dry-run text response" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_dry_run_allows_structured_llm_json_used_as_number() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build token workflow"
                prefilter: false
              validate:
                dry_run: true
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: extract
            type: llm.call
            input:
              prompt: "Return a token budget"
              structured_output:
                schema_inline:
                  type: object
                  properties:
                    max_tokens:
                      type: integer
                  required: [max_tokens]
                  additionalProperties: false
          - id: answer
            type: llm.call
            input:
              prompt: "Answer briefly"
              max_tokens: "${data.steps.extract.json.max_tokens}"
        outputs:
          answer:
            expr: "${data.steps.answer.text}"
            type: string
    """

    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True


@pytest.mark.asyncio
async def test_workflow_plan_pipeline_mode_composes_main_and_leaf_subworkflows() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              mode: pipeline
              raw_prompt: "Resolve issue ABC-42"
              generator:
                model: fake
                prefilter: false
              validate:
                compile: true
    """
    leaf_yaml = """
    version: 1
    name: parse_issue_leaf
    skill:
      description: Parse issue.
      tags: [issue]
      inputs:
        issue_key: string
      outputs:
        issue_number: integer
    workflows:
      parse_issue:
        inputs:
          issue_key: string
        steps:
          - id: parsed
            type: set
            input:
              issue_number: 42
        outputs:
          issue_number: "${data.steps.parsed.issue_number}"
    """
    assembly_yaml = """
    document:
      name: issue_pipeline
      skill:
        description: Resolve an issue.
        tags: [issue, pipeline]
        inputs:
          issue_key: string
        outputs:
          issue_number: integer
    main:
      inputs:
        issue_key: string
      steps:
        - id: call_parse_issue
          type: workflow.call
          input:
            ref:
              kind: local
              name: parse_issue
            args:
              issue_key: "${data.inputs.issue_key}"
      outputs:
        issue_number: "${data.steps.call_parse_issue.outputs.issue_number}"
    """

    engine = WorkflowEngine()
    llm = PipelinePlanLlm(leaf_yaml, assembly_yaml)
    engine.llm_client = llm

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    plan = result.outputs["plan"]
    assert plan["meta"]["mode"] == "pipeline"
    generated = yaml.safe_load(plan["yaml"])
    assert list(generated["workflows"].keys()) == ["main", "parse_issue"]
    assert generated["entrypoint"] == "main"
    assert generated["workflows"]["main"]["steps"][0]["type"] == "workflow.call"
    assert generated["workflows"]["main"]["steps"][0]["input"]["ref"]["name"] == "parse_issue"
    leaf_prompt = next(prompt for prompt in llm.prompts if "Generate exactly one leaf GnOuGo workflow named `parse_issue`" in prompt)
    assert "Treat the declared input/output contract as a draft when MCP tools require additional arguments." in leaf_prompt
    assert "1. Inspect every MCP tool used by this workflow." in leaf_prompt
    assert "Never convert a string input to a number just to satisfy an MCP schema." in leaf_prompt
    assert "Workflow outputs must match their declared contract type exactly on every path." in leaf_prompt
    assert "If a step has an `if`, later unconditional steps must not reference that step directly." in leaf_prompt
    assembly_prompt = next(prompt for prompt in llm.prompts if "assembling the parent `main` workflow" in prompt)
    assert "generated_leaf_contracts_yaml" in assembly_prompt
    assert "`generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs." in assembly_prompt
    assert "issue_key: string" in assembly_prompt


@pytest.mark.asyncio
async def test_workflow_plan_pipeline_mode_rejects_generated_leaf_workflow_calls() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              mode: pipeline
              raw_prompt: "Resolve issue ABC-42"
              generator:
                model: fake
                prefilter: false
              on_invalid:
                action: fail
                max_attempts: 1
    """
    leaf_yaml = """
    version: 1
    name: bad_leaf
    skill:
      description: Bad leaf.
      tags: [issue]
      inputs:
        issue_key: string
      outputs:
        issue_number: integer
    workflows:
      parse_issue:
        inputs:
          issue_key: string
        steps:
          - id: call_nested
            type: workflow.call
            input:
              ref:
                kind: local
                name: other
              args: {}
        outputs:
          issue_number: 1
    """
    assembly_yaml = "document: {name: unused}\nmain: {steps: []}\n"

    engine = WorkflowEngine()
    engine.llm_client = PipelinePlanLlm(leaf_yaml, assembly_yaml)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "Leaf workflow 'parse_issue'" in result.error.message
    assert "workflow.call" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_pipeline_mode_final_dry_run_accepts_string_conversion_helper() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              mode: pipeline
              raw_prompt: "Resolve issue ABC-42"
              generator:
                model: fake
                prefilter: false
              validate:
                compile: true
                dry_run: true
    """
    leaf_yaml = """
    version: 1
    name: parse_issue_leaf
    skill:
      description: Parse issue.
      tags: [issue]
      inputs:
        issue_key: string
      outputs:
        issue_number: integer
    workflows:
      parse_issue:
        inputs:
          issue_key: string
        steps:
          - id: parsed
            type: set
            input:
              issue_number: 42
        outputs:
          issue_number: "${data.steps.parsed.issue_number}"
    """
    assembly_yaml = """
    document:
      name: issue_pipeline
      skill:
        description: Resolve an issue.
        tags: [issue, pipeline]
        inputs:
          issue_key: string
        outputs:
          answer: string
    main:
      inputs:
        issue_key: string
      steps:
        - id: call_parse_issue
          type: workflow.call
          input:
            ref:
              kind: local
              name: parse_issue
            args:
              issue_key: "${data.inputs.issue_key}"
        - id: answer
          type: llm.call
          input:
            prompt: "Issue number is ${string(data.steps.call_parse_issue.outputs.issue_number)}"
      outputs:
        answer: "${data.steps.answer.text}"
    """

    engine = WorkflowEngine()
    engine.llm_client = PipelinePlanLlm(leaf_yaml, assembly_yaml)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_unknown_mcp_method_in_methods_array() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              methods: [get_doc, missing_doc]
              request:
                id: intro
    """
    tool = McpToolInfo(name="get_doc", description="Get a document")
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "MCP_METHOD_UNKNOWN" in result.error.message
    assert "input.methods[1]:missing_doc" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_unknown_mcp_call_input_field() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
              id: intro
    """
    tool = McpToolInfo(name="get_doc", description="Get a document")
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "MCP_CALL_INPUT_FIELD_UNKNOWN" in result.error.message
    assert "input.id" in result.error.message
    assert "input.request" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_semantic_validation_rejects_invalid_llm_structured_output_schema() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build typed llm workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: classify
            type: llm.call
            input:
              prompt: "Classify"
              structured_output:
                strict: true
                schema_inline:
                  type: object
                  properties:
                    category: { type: string }
                    priority: { type: string }
                  required: [category]
                  additionalProperties: false
    """
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "TEMPLATE_PLAN"
    assert "STRUCTURED_OUTPUT_SCHEMA_INVALID" in result.error.message
    assert "structured_output" in result.error.message
    assert "priority" in result.error.message


@pytest.mark.asyncio
async def test_workflow_plan_mcp_discovery_retries_before_validation() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
    """
    generated_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
              request:
                id: intro
    """
    tool = McpToolInfo(name="get_doc", description="Get a document")
    factory = FlakyDocsMcpFactory(tool)
    engine = WorkflowEngine()
    engine.llm_client = CapturePlanLlm(generated_yaml)
    engine.mcp_client_factory = factory

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    assert factory.calls >= 3


@pytest.mark.asyncio
async def test_workflow_plan_retry_prompt_includes_targeted_mcp_tools_for_unknown_method() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
              on_invalid:
                action: reprompt
                max_attempts: 2
    """
    invalid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: missing_doc
              request:
                id: intro
    """
    valid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
              request:
                id: intro
    """
    tool = McpToolInfo(
        name="get_doc",
        description="Get a document by id",
        input_schema={
            "type": "object",
            "required": ["id"],
            "properties": {"id": {"type": "string"}},
            "additionalProperties": False,
        },
        output_schema={"type": "object", "properties": {"title": {"type": "string"}}, "additionalProperties": False},
    )
    llm = SequencePlanLlm([invalid_yaml, valid_yaml])
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    assert len(llm.prompts) == 2
    retry_prompt = llm.prompts[1]
    assert "MCP docs for failed/referenced calls" in retry_prompt
    assert "Available MCP servers: docs" in retry_prompt
    assert "Step `fetch` references MCP server `docs`" in retry_prompt
    assert "Available tools on `docs`: get_doc" in retry_prompt
    assert "Unknown requested method(s): missing_doc" in retry_prompt
    assert "- get_doc: Get a document by id" in retry_prompt
    assert "input_schema_json" in retry_prompt
    assert "output_schema_json" in retry_prompt
    assert "method: <exact-tool>" in retry_prompt


@pytest.mark.asyncio
async def test_workflow_plan_retry_prompt_includes_invalid_mcp_request_and_schema() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: plan
            type: workflow.plan
            input:
              generator:
                model: fake
                instruction: "build docs workflow"
                prefilter: false
              on_invalid:
                action: reprompt
                max_attempts: 2
    """
    invalid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
              request:
                title: intro
    """
    valid_yaml = """
    version: 1
    workflows:
      main:
        steps:
          - id: fetch
            type: mcp.call
            input:
              server: docs
              method: get_doc
              request:
                id: intro
    """
    tool = McpToolInfo(
        name="get_doc",
        description="Get a document by id",
        input_schema={
            "type": "object",
            "required": ["id"],
            "properties": {"id": {"type": "string"}},
            "additionalProperties": False,
        },
    )
    llm = SequencePlanLlm([invalid_yaml, valid_yaml])
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = DocsMcpFactory(tool)

    result = await engine.execute_async(WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"], {})

    assert result.success is True
    retry_prompt = llm.prompts[1]
    assert "MCP_REQUEST_SCHEMA_INVALID" in retry_prompt
    assert "invalid_request_yaml" in retry_prompt
    assert "title: intro" in retry_prompt
    assert "input_schema_json" in retry_prompt
    assert "For every listed input_schema_json, copy all required request properties into input.request" in retry_prompt
    assert "When repairing one MCP call, re-check every MCP call in the YAML" in retry_prompt
    assert '"required": [' in retry_prompt
    assert '"id"' in retry_prompt
