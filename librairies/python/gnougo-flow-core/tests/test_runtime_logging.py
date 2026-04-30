import logging

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _compile(yaml_text: str):
    return WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))


@pytest.mark.asyncio
async def test_workflow_logs_start_and_completion(caplog) -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: a
            type: set
            input: { x: 1 }
    """
    compiled = _compile(yaml_text)
    engine = WorkflowEngine()
    with caplog.at_level(logging.INFO, logger="gnougo_flow_core"):
        result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True
    msgs = [r.getMessage() for r in caplog.records]
    assert any("starting" in m and "main" in m for m in msgs)
    assert any("completed successfully" in m for m in msgs)


@pytest.mark.asyncio
async def test_step_failure_logs_error(caplog) -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: bad
            type: set
            input: "not-an-object"
    """
    compiled = _compile(yaml_text)
    engine = WorkflowEngine()
    with caplog.at_level(logging.ERROR, logger="gnougo_flow_core"):
        result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert any(
        r.levelno == logging.ERROR and "INPUT_VALIDATION" in r.getMessage()
        for r in caplog.records
    )


@pytest.mark.asyncio
async def test_logger_overridable() -> None:
    custom = logging.getLogger("test_custom_engine_logger")
    custom.setLevel(logging.INFO)
    records: list[logging.LogRecord] = []

    class _Handler(logging.Handler):
        def emit(self, record):
            records.append(record)

    h = _Handler()
    custom.addHandler(h)
    try:
        engine = WorkflowEngine()
        engine.logger = custom
        compiled = _compile(
            """
            version: 1
            workflows:
              main:
                steps:
                  - id: a
                    type: set
                    input: { x: 1 }
            """
        )
        await engine.execute_async(compiled.workflows["main"], {})
    finally:
        custom.removeHandler(h)

    assert any("starting" in r.getMessage() for r in records)

