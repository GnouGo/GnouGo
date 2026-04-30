
import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import FetchPolicy
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _compile(yaml_text: str):
    return WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))


# ---------- local ----------

@pytest.mark.asyncio
async def test_workflow_call_local_passes_args_and_returns_outputs() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: local, name: helper }
              args: { x: 7 }
        outputs:
          r: "${data.steps.c.outputs.doubled}"
      helper:
        inputs:
          x: { type: number }
        steps:
          - id: d
            type: set
            input: { doubled: "${data.inputs.x * 2}" }
        outputs:
          doubled: "${data.steps.d.doubled}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    engine.compiled_document = compiled
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["r"] == 14


@pytest.mark.asyncio
async def test_workflow_call_local_cycle_detected() -> None:
    # Mutual a ? b ? a cycle is rejected at runtime when the second hop tries
    # to re-enter a workflow already in the call stack. (The compiler also
    # catches some static cycles, but mutual-recursion cycles via workflow.call
    # are detected here at runtime.)
    yaml_text = """
    version: 1
    workflows:
      a:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: local, name: b }
      b:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: local, name: a }
    """
    engine = WorkflowEngine()
    try:
        compiled = _compile(yaml_text)
    except Exception as exc:
        # If the static analyser catches it, accept that — the cycle code is reported.
        assert "WORKFLOW_CYCLE_DETECTED" in str(exc)
        return
    engine.compiled_document = compiled
    result = await engine.execute_async(compiled.workflows["a"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_CYCLE_DETECTED"


@pytest.mark.asyncio
async def test_workflow_call_max_call_depth_exceeded() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: local, name: helper }
      helper:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: local, name: helper2 }
      helper2:
        steps:
          - id: ok
            type: set
            input: { v: 1 }
    """
    engine = WorkflowEngine()
    engine.limits.max_call_depth = 1
    compiled = _compile(yaml_text)
    engine.compiled_document = compiled
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_CYCLE_DETECTED"
    assert "Max call depth" in result.error.message


@pytest.mark.asyncio
async def test_workflow_call_local_unknown_workflow() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: local, name: missing }
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    engine.compiled_document = compiled
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "INPUT_VALIDATION"
    assert "missing" in result.error.message


@pytest.mark.asyncio
async def test_workflow_call_args_are_deep_copied() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: prep
            type: set
            input:
              shared: { count: 1 }
          - id: c
            type: workflow.call
            input:
              ref: { kind: local, name: helper }
              args: { obj: "${data.steps.prep.shared}" }
        outputs:
          parent_count: "${data.steps.prep.shared.count}"
          sub_count: "${data.steps.c.outputs.observed_count}"
      helper:
        steps:
          - id: read
            type: set
            input: { observed_count: "${data.inputs.obj.count}" }
        outputs:
          observed_count: "${data.steps.read.observed_count}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    engine.compiled_document = compiled
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["parent_count"] == 1
    assert result.outputs["sub_count"] == 1


# ---------- remote ----------

class _StaticFetcher:
    def __init__(self, yaml_text: str):
        self._yaml = yaml_text
        self.calls: list[tuple[str, str | None]] = []

    async def fetch_async(self, url: str, integrity: str | None) -> str:
        self.calls.append((url, integrity))
        return self._yaml


@pytest.mark.asyncio
async def test_workflow_call_remote_no_fetcher() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: url, url: "https://example.com/wf.yaml" }
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_FETCH_NETWORK"


@pytest.mark.asyncio
async def test_workflow_call_remote_requires_https() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: url, url: "http://example.com/wf.yaml" }
    """
    engine = WorkflowEngine()
    engine.workflow_fetcher = _StaticFetcher("version: 1\nworkflows: { main: { steps: [] } }")
    engine.fetch_policy = FetchPolicy(require_https=True)
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_FETCH_POLICY"
    assert "HTTPS" in result.error.message


@pytest.mark.asyncio
async def test_workflow_call_remote_host_not_in_allow_list() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: url, url: "https://evil.example/wf.yaml" }
    """
    engine = WorkflowEngine()
    engine.workflow_fetcher = _StaticFetcher("version: 1\nworkflows: { main: { steps: [] } }")
    engine.fetch_policy = FetchPolicy(require_https=True, allowed_hostnames=["good.example"])
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_FETCH_POLICY"
    assert "allow-list" in result.error.message


@pytest.mark.asyncio
async def test_workflow_call_remote_integrity_required() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: url, url: "https://good.example/wf.yaml" }
    """
    engine = WorkflowEngine()
    engine.workflow_fetcher = _StaticFetcher("version: 1\nworkflows: { main: { steps: [] } }")
    engine.fetch_policy = FetchPolicy(
        require_https=True, allowed_hostnames=["good.example"], require_integrity=True
    )
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_FETCH_POLICY"
    assert "Integrity" in result.error.message


@pytest.mark.asyncio
async def test_workflow_call_remote_exceeds_max_size() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref: { kind: url, url: "https://good.example/wf.yaml" }
    """
    big_yaml = "version: 1\nworkflows:\n  main:\n    steps: []\n# " + "x" * 1024
    engine = WorkflowEngine()
    engine.workflow_fetcher = _StaticFetcher(big_yaml)
    engine.fetch_policy = FetchPolicy(
        require_https=True, allowed_hostnames=["good.example"], max_size_bytes=64
    )
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_FETCH_POLICY"
    assert "max_size_bytes" in result.error.message


@pytest.mark.asyncio
async def test_workflow_call_remote_export_not_in_doc() -> None:
    remote_yaml = """
    version: 1
    exports: [public]
    workflows:
      public:
        steps:
          - id: a
            type: set
            input: { v: 1 }
      private:
        steps:
          - id: b
            type: set
            input: { v: 2 }
    """
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref:
                kind: url
                url: "https://good.example/wf.yaml"
                export: private
    """
    engine = WorkflowEngine()
    engine.workflow_fetcher = _StaticFetcher(remote_yaml)
    engine.fetch_policy = FetchPolicy(require_https=True, allowed_hostnames=["good.example"])
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "WORKFLOW_FETCH_POLICY"
    assert "private" in result.error.message


@pytest.mark.asyncio
async def test_workflow_call_remote_selects_export() -> None:
    remote_yaml = """
    version: 1
    exports: [public]
    workflows:
      public:
        steps:
          - id: a
            type: set
            input: { v: 42 }
        outputs:
          v: "${data.steps.a.v}"
    """
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: c
            type: workflow.call
            input:
              ref:
                kind: url
                url: "https://good.example/wf.yaml"
                export: public
        outputs:
          val: "${data.steps.c.outputs.v}"
    """
    engine = WorkflowEngine()
    engine.workflow_fetcher = _StaticFetcher(remote_yaml)
    engine.fetch_policy = FetchPolicy(require_https=True, allowed_hostnames=["good.example"])
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["val"] == 42


