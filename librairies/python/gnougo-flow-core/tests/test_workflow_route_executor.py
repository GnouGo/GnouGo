import json

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import ExecutionLimits, LLMRequest, LLMResponse, WorkflowRouteCandidate
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import ITelemetrySpan, WorkflowEngine
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


class _CaptureSpan(ITelemetrySpan):
    def __init__(self, name: str, events: list[tuple[str, list[tuple[str, object]]]]) -> None:
        self.name = name
        self.events = events

    def add_event(self, name: str, attributes=None):
        self.events.append((name, list(attributes or [])))


class _CaptureTelemetry:
    def __init__(self) -> None:
        self.events: list[tuple[str, list[tuple[str, object]]]] = []

    def workflow_start(self, info):
        return _CaptureSpan(str(info.get("workflow_name", "workflow")), self.events)

    def workflow_end(self, span, info):
        return

    def step_start(self, parent, info):
        return _CaptureSpan(str(info.get("step_id", "step")), self.events)

    def step_end(self, span, info):
        return


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
                      api_key: { type: string }
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
          api_key: { type: string, required: false }
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
    llm = _ExtractingLlmClient(
        {
            "arguments": {
                "repository_path": "/tmp/repo",
                "base_branch": "develop",
                "api_key": "secret-value",
                "unexpected": "discard me",
            }
        }
    )
    telemetry = _CaptureTelemetry()
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.telemetry = telemetry
    engine.lm_defaults.provider = "default-provider"
    engine.lm_defaults.model = "default-model"
    engine.limits.log_step_content = True

    result = await engine.execute_async(
        _compile(yaml_text),
        {
            "prompt": "compare this repo with develop",
            "task": "parent alias should not leak",
            "query": "parent alias should not leak",
            "message": "parent alias should not leak",
        },
    )

    assert result.success is True, result.error
    assert result.outputs["answer"] == "/tmp/repo vs develop: compare this repo with develop"
    assert len(llm.requests) == 1
    assert llm.requests[0].provider == "test-provider"
    assert llm.requests[0].model == "test-model"
    assert llm.requests[0].temperature == 0.1
    assert "repository_path" in llm.requests[0].prompt
    routed_events = [
        dict(attributes)
        for name, attributes in telemetry.events
        if name == "gnougo-flow.workflow_route.inputs_extracted"
    ]
    assert len(routed_events) == 1
    event = routed_events[0]
    assert event["gnougo-flow.workflow_route.candidate.id"] == "local:inspect_repo"
    assert event["gnougo-flow.workflow_route.workflow.name"] == "inspect_repo"
    assert event["gnougo-flow.workflow_route.auto_extract.enabled"] is True
    arguments = json.loads(event["gnougo-flow.workflow_route.arguments"])
    assert arguments["repository_path"] == "/tmp/repo"
    assert arguments["api_key"] == "<redacted>"
    assert "unexpected" not in arguments
    assert "task" not in arguments
    assert "query" not in arguments
    assert "message" not in arguments
    resolved_inputs = json.loads(event["gnougo-flow.workflow_route.resolved_inputs"])
    assert resolved_inputs["base_branch"] == "develop"
    assert "unexpected" not in resolved_inputs
    thinking_events = [
        dict(attributes)
        for name, attributes in telemetry.events
        if name == "gnougo-flow.step.thinking"
    ]
    assert len(thinking_events) == 1
    thinking = thinking_events[0]
    assert thinking["gnougo-flow.thinking.level"] == "progress"
    assert thinking["gnougo-flow.thinking.source"] == "workflow.route"
    assert thinking["gnougo-flow.workflow_route.workflow.name"] == "inspect_repo"
    assert (
        thinking["gnougo-flow.thinking.message"]
        == "Triggering workflow 'inspect_repo' with inputs "
        + event["gnougo-flow.workflow_route.resolved_inputs"]
    )
    thinking_inputs = json.loads(thinking["gnougo-flow.workflow_route.resolved_inputs"])
    assert thinking_inputs["api_key"] == "<redacted>"


@pytest.mark.asyncio
async def test_workflow_route_uses_workflow_inputs_as_authoritative_auto_extract_target() -> None:
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
                - ref: { kind: local, name: issue_resolver }
                  description: Resolves GitHub issues.
                  inputs:
                    type: object
                    properties:
                      task: { type: string }
                      repo_url: { type: string, default: "https://github.com/AxaFrance/oidc-client" }
                      max_issues: { type: string, default: "4" }
              args:
                passthrough: false
                auto_extract: true
              combine:
                strategy: first
        outputs:
          answer:
            expr: "${data.steps.route.answer}"
            type: string
      issue_resolver:
        inputs:
          task: { type: string, required: true }
          repo_url: { type: string, required: false }
          max_issues: { type: string, required: false }
        steps:
          - id: render
            type: template.render
            input:
              engine: mustache
              mode: text
              template: "{{max_issues}} issues from {{repo_url}} for {{task}}"
              data:
                task: "${data.inputs.task}"
                repo_url: "${data.inputs.repo_url}"
                max_issues: "${data.inputs.max_issues}"
        outputs:
          answer:
            expr: "${data.steps.render.text}"
            type: string
    """
    llm = _ExtractingLlmClient(
        {
            "arguments": {
                "task": "Resolve open issues",
                "repo_url": "https://github.com/AxaFrance/axa-fr-oidc",
                "max_issues": "20",
            }
        }
    )
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(
        _compile(yaml_text),
        {"prompt": "Resolve the first 20 issues on https://github.com/AxaFrance/axa-fr-oidc/"},
    )

    assert result.success is True, result.error
    assert result.outputs["answer"] == "20 issues from https://github.com/AxaFrance/axa-fr-oidc for Resolve open issues"
    assert len(llm.requests) == 1
    assert "repo_url" in llm.requests[0].prompt
    assert "max_issues" in llm.requests[0].prompt


@pytest.mark.asyncio
async def test_workflow_route_validation_fails_when_extracted_input_type_is_invalid() -> None:
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
                - ref: { kind: local, name: issue_resolver }
                  description: Resolves GitHub issues.
              args:
                passthrough: false
                auto_extract: true
              combine:
                strategy: first
      issue_resolver:
        inputs:
          task: { type: string, required: true }
          max_issues: { type: number, required: true }
        steps:
          - id: render
            type: template.render
            input:
              engine: mustache
              mode: text
              template: "{{max_issues}} issues for {{task}}"
              data:
                task: "${data.inputs.task}"
                max_issues: "${data.inputs.max_issues}"
    """
    llm = _ExtractingLlmClient(
        {
            "arguments": {
                "task": "Resolve open issues",
                "max_issues": "twenty",
            }
        }
    )
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(_compile(yaml_text), {"prompt": "Resolve twenty issues"})

    assert result.success is False
    assert result.error is not None
    assert result.error.code == "INPUT_VALIDATION"
    assert "Input validation failed for routed workflow 'issue_resolver'" in result.error.message
    assert "'max_issues': expected number, got string." in result.error.message


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
              mode: text
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
              mode: text
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
