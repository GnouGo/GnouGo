import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import TemplateResult
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _compile(yaml_text: str):
    return WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))


@pytest.mark.asyncio
async def test_text_mode_default() -> None:
    compiled = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: r
                type: template.render
                input:
                  template: "Hi {{name}}"
                  data: { name: World }
            outputs: {}
        outputs:
          out: "${data.steps.r.text}"
        """
    )
    # outputs at workflow level
    compiled = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: r
                type: template.render
                input:
                  template: "Hi {{name}}"
                  data: { name: World }
            outputs:
              out: "${data.steps.r.text}"
              meta_engine: "${data.steps.r.meta.engine}"
              meta_mode: "${data.steps.r.meta.mode}"
        """
    )
    engine = WorkflowEngine()
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["out"] == "Hi World"
    assert result.outputs["meta_engine"] == "mustache"
    assert result.outputs["meta_mode"] == "text"


@pytest.mark.asyncio
async def test_json_mode_parses_output() -> None:
    compiled = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: r
                type: template.render
                input:
                  template: '{"a":{{n}}}'
                  data: { n: 7 }
                  mode: json
            outputs:
              json_payload: "${data.steps.r.json}"
        """
    )
    engine = WorkflowEngine()
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["json_payload"] == {"a": 7}


@pytest.mark.asyncio
async def test_json_mode_invalid_raises_json_parse() -> None:
    compiled = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: r
                type: template.render
                input:
                  template: "not-json {{x}}"
                  data: { x: 1 }
                  mode: json
        """
    )
    engine = WorkflowEngine()
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "JSON_PARSE"


@pytest.mark.asyncio
async def test_markdown_and_html_return_text_with_mode_meta() -> None:
    for mode in ("markdown", "html"):
        compiled = _compile(
            f"""
            version: 1
            workflows:
              main:
                steps:
                  - id: r
                    type: template.render
                    input:
                      template: "X={{{{x}}}}"
                      data: {{ x: 1 }}
                      mode: {mode}
                outputs:
                  text: "${{data.steps.r.text}}"
                  m: "${{data.steps.r.meta.mode}}"
            """
        )
        engine = WorkflowEngine()
        result = await engine.execute_async(compiled.workflows["main"], {})
        assert result.success is True, result.error
        assert result.outputs["text"] == "X=1"
        assert result.outputs["m"] == mode


@pytest.mark.asyncio
async def test_unknown_mode_raises_input_validation() -> None:
    compiled = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: r
                type: template.render
                input:
                  template: "x"
                  mode: weird
        """
    )
    engine = WorkflowEngine()
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "INPUT_VALIDATION"


class _FakeTemplateEngine:
    def __init__(self) -> None:
        self.calls: list[tuple[str, object, bool, str]] = []

    async def render_async(self, template, data, strict, mode):
        self.calls.append((template, data, strict, mode))
        return TemplateResult(text="from-engine", meta={"engine": "fake"})


@pytest.mark.asyncio
async def test_routes_through_custom_template_engine() -> None:
    compiled = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: r
                type: template.render
                input:
                  template: "ignored"
                  data: { x: 1 }
                  mode: text
            outputs:
              t: "${data.steps.r.text}"
              eng: "${data.steps.r.meta.engine}"
        """
    )
    engine = WorkflowEngine()
    fake = _FakeTemplateEngine()
    engine.template_engine = fake
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["t"] == "from-engine"
    assert result.outputs["eng"] == "fake"
    assert len(fake.calls) == 1

