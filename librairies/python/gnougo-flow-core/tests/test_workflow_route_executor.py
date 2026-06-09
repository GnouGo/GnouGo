import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import ExecutionLimits, LLMRequest, LLMResponse, WorkflowRouteCandidate
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.workflow_call_resolver import DefaultWorkflowCallResolver, WorkflowCallResolution


def _compile(yaml_text: str):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


class _SelectingLlmClient:
    def __init__(self, *selected_ids: str):
        self.selected_ids = selected_ids
        self.requests: list[LLMRequest] = []

    async def call_async(self, request: LLMRequest) -> LLMResponse:
        self.requests.append(request)
        return LLMResponse(
            json={
                "selected": [
                    {"id": selected_id, "reason": "matches", "confidence": 0.9}
                    for selected_id in self.selected_ids
                ]
            }
        )


class _ExtractingLlmClient:
    def __init__(self, response: dict):
        self.response = response
        self.requests: list[LLMRequest] = []

    async def call_async(self, request: LLMRequest) -> LLMResponse:
        self.requests.append(request)
        return LLMResponse(json=self.response)


class _FakeCandidateProvider:
    def __init__(self, *candidates: WorkflowRouteCandidate):
        self.candidates = list(candidates)
        self.queries = []

    async def get_candidates_async(self, query):
        self.queries.append(query)
        return self.candidates


class _FakeWorkflowCallResolver(DefaultWorkflowCallResolver):
    def __init__(self, *workflows):
        self.database_workflows = {name.lower(): workflow for name, workflow in workflows}
        super().__init__()

    async def resolve_async(self, context):
        if context.kind.lower() == "database":
            agent = context.ref.get("agent", "")
            workflow = self.database_workflows[agent.lower()]
            return WorkflowCallResolution(workflow, agent, f"database:{agent}")
        return await super().resolve_async(context)


class _CapturingHumanInputProvider:
    def __init__(self):
        self.requests = []

    async def request_input_async(self, request):
        self.requests.append(request)
        return {"response": request.prompt}


@pytest.mark.asyncio
async def test_workflow_route_expands_database_candidates_and_calls_selected_workflow() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        inputs:
          prompt: { type: string, required: true }
        steps:
          - id: route
            type: workflow.route
            input:
              prompt: "${data.inputs.prompt}"
              candidates:
                - ref: { kind: database }
                  tags_any: [git]
              selection:
                mode: single
                min: 1
                max: 1
              args:
                passthrough: true
              combine:
                strategy: first
        outputs:
          answer:
            expr: "${data.steps.route.answer}"
            type: string
    """
    agent_yaml = """
    version: 1
    skill:
      description: Inspects git repositories.
      tags: [git, code]
    workflows:
      main:
        inputs:
          prompt: { type: string, required: true }
        steps:
          - id: render
            type: template.render
            input:
              engine: mustache
              mode: text
              template: "git answer: {{prompt}}"
              data:
                prompt: "${data.inputs.prompt}"
        outputs:
          answer:
            expr: "${data.steps.render.text}"
            type: string
    """

    agent = _compile(agent_yaml)
    provider = _FakeCandidateProvider(
        WorkflowRouteCandidate(
            id="database:GitAgent",
            name="GitAgent",
            ref={"kind": "database", "agent": "GitAgent"},
            description="Inspects git repositories.",
            tags=["git", "code"],
        )
    )
    engine = WorkflowEngine()
    engine.llm_client = _SelectingLlmClient("database:GitAgent")
    engine.workflow_candidate_provider = provider
    engine.workflow_call_resolver = _FakeWorkflowCallResolver(("GitAgent", agent))

    result = await engine.execute_async(_compile(yaml_text), {"prompt": "show diff"})

    assert result.success is True, result.error
    assert result.outputs["answer"] == "git answer: show diff"
    assert len(provider.queries) == 1
    assert provider.queries[0].tags_any == ["git"]


@pytest.mark.asyncio
async def test_workflow_route_uses_static_fallback_when_dynamic_catalog_is_empty() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        inputs:
          prompt: { type: string, required: true }
        steps:
          - id: route
            type: workflow.route
            input:
              prompt: "${data.inputs.prompt}"
              candidates:
                - ref: { kind: database }
                - ref: { kind: local, name: fallback }
                  description: General fallback.
              combine:
                strategy: first
        outputs:
          answer:
            expr: "${data.steps.route.answer}"
            type: string
      fallback:
        inputs:
          prompt: { type: string, required: true }
        steps:
          - id: render
            type: template.render
            input:
              engine: mustache
              mode: text
              template: "fallback: {{prompt}}"
              data:
                prompt: "${data.inputs.prompt}"
        outputs:
          answer:
            expr: "${data.steps.render.text}"
            type: string
    """
    engine = WorkflowEngine()
    engine.workflow_candidate_provider = _FakeCandidateProvider()

    result = await engine.execute_async(_compile(yaml_text), {"prompt": "hello"})

    assert result.success is True, result.error
    assert result.outputs["answer"] == "fallback: hello"


@pytest.mark.asyncio
async def test_workflow_route_auto_extracts_selected_workflow_arguments_with_configured_model() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        inputs:
          prompt: { type: string, required: true }
        steps:
          - id: route
            type: workflow.route
            input:
              prompt: "${data.inputs.prompt}"
              candidates:
                - ref: { kind: local, name: inspect_repo }
                  description: Inspects a git repository.
                  inputs:
                    type: object
                    properties:
                      prompt: { type: string }
                      repository_path: { type: string }
                      base_branch: { type: string }
              args:
                passthrough: true
                auto_extract:
                  provider: test-provider
                  model: test-model
                  temperature: 0.1
              combine:
                strategy: first
        outputs:
          answer:
            expr: "${data.steps.route.answer}"
            type: string
      inspect_repo:
        inputs:
          prompt: { type: string, required: true }
          repository_path: { type: string, required: true }
          base_branch: { type: string, required: false, default: main }
        steps:
          - id: render
            type: template.render
            input:
              engine: mustache
              mode: text
              template: "{{repository_path}} vs {{base_branch}}: {{prompt}}"
              data:
                repository_path: "${data.inputs.repository_path}"
                base_branch: "${data.inputs.base_branch}"
                prompt: "${data.inputs.prompt}"
        outputs:
          answer:
            expr: "${data.steps.render.text}"
            type: string
    """
    llm = _ExtractingLlmClient({"arguments": {"repository_path": "/tmp/repo", "base_branch": "develop"}})
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.lm_defaults.provider = "default-provider"
    engine.lm_defaults.model = "default-model"

    result = await engine.execute_async(_compile(yaml_text), {"prompt": "compare this repo with develop"})

    assert result.success is True, result.error
    assert result.outputs["answer"] == "/tmp/repo vs develop: compare this repo with develop"
    assert len(llm.requests) == 1
    assert llm.requests[0].provider == "test-provider"
    assert llm.requests[0].model == "test-model"
    assert llm.requests[0].temperature == 0.1
    assert "repository_path" in llm.requests[0].prompt


@pytest.mark.asyncio
async def test_workflow_route_uses_distinct_run_ids_for_parallel_human_input_candidates() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        inputs:
          prompt: { type: string, required: true }
        steps:
          - id: route
            type: workflow.route
            input:
              prompt: "${data.inputs.prompt}"
              candidates:
                - ref: { kind: local, name: first }
                  description: First interactive workflow.
                - ref: { kind: local, name: second }
                  description: Second interactive workflow.
              selection:
                mode: multiple
                min: 2
                max: 2
              execution:
                parallel: true
                max_concurrency: 2
              combine:
                strategy: raw
      first:
        steps:
          - id: ask_user
            type: human.input
            input:
              prompt: First question?
        outputs:
          answer:
            expr: "${data.steps.ask_user.response}"
            type: string
      second:
        steps:
          - id: ask_user
            type: human.input
            input:
              prompt: Second question?
        outputs:
          answer:
            expr: "${data.steps.ask_user.response}"
            type: string
    """
    human_input = _CapturingHumanInputProvider()
    engine = WorkflowEngine()
    engine.llm_client = _SelectingLlmClient("local:first", "local:second")
    engine.human_input_provider = human_input
    engine.limits = ExecutionLimits(run_id="parent-run")

    result = await engine.execute_async(_compile(yaml_text), {"prompt": "ask both"})

    assert result.success is True, result.error
    assert len(human_input.requests) == 2
    assert all(request.step_id == "ask_user" for request in human_input.requests)
    assert all(request.run_id.startswith("parent-run:route:") for request in human_input.requests)
    assert len({request.run_id for request in human_input.requests}) == 2
