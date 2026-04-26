from __future__ import annotations

import json

import pytest

from gnougo_flow_core.cli import main

SIMPLE_WORKFLOW = """
dsl: 1
name: cli-demo
workflows:
  main:
    inputs:
      name:
        type: string
        required: false
        default: Alice
      count:
        type: number
        required: false
        default: 1
    steps:
      - id: greet
        type: set
        input:
          message: "Hello ${data.inputs.name}"
          count: "${data.inputs.count}"
    outputs:
      message: "${data.steps.greet.message}"
      count: "${data.steps.greet.count}"
"""


def _run_cli(argv: list[str]) -> int:
    with pytest.raises(SystemExit) as exc:
        main(argv)
    return int(exc.value.code or 0)


def test_cli_validate_success(tmp_path, capsys) -> None:
    path = tmp_path / "workflow.yaml"
    path.write_text(SIMPLE_WORKFLOW, encoding="utf-8")

    code = _run_cli(["validate", str(path)])

    assert code == 0
    assert "Validation passed" in capsys.readouterr().out


def test_cli_validate_reports_errors(tmp_path, capsys) -> None:
    path = tmp_path / "workflow.yaml"
    path.write_text("dsl: 1\nworkflows: {}\n", encoding="utf-8")

    code = _run_cli(["validate", str(path)])

    assert code == 1
    assert "validation error" in capsys.readouterr().out


def test_cli_inspect_prints_structure(tmp_path, capsys) -> None:
    path = tmp_path / "workflow.yaml"
    path.write_text(SIMPLE_WORKFLOW, encoding="utf-8")

    code = _run_cli(["inspect", str(path)])

    out = capsys.readouterr().out
    assert code == 0
    assert "Version: 1" in out
    assert "Workflow: main" in out
    assert "Inputs:" in out
    assert "Outputs:" in out
    assert "[set] greet" in out


def test_cli_run_accepts_repeatable_key_value_inputs_and_outputs_json(tmp_path, capsys) -> None:
    path = tmp_path / "workflow.yaml"
    path.write_text(SIMPLE_WORKFLOW, encoding="utf-8")

    code = _run_cli(["run", str(path), "-i", "name=Bob", "-i", "count=3"])

    out = capsys.readouterr().out
    assert code == 0
    assert "Running workflow 'main'" in out
    assert "Workflow completed successfully" in out
    assert "Steps executed:" in out
    assert "Outputs:" in out
    outputs = json.loads(out.split("Outputs:\n", 1)[1])
    assert outputs == {"message": "Hello Bob", "count": 3}


def test_cli_run_accepts_json_input_inline_and_file(tmp_path, capsys) -> None:
    workflow_path = tmp_path / "workflow.yaml"
    workflow_path.write_text(SIMPLE_WORKFLOW, encoding="utf-8")
    json_path = tmp_path / "inputs.json"
    json_path.write_text('{"name":"Carol","count":5}', encoding="utf-8")

    inline_code = _run_cli(["run", str(workflow_path), "-j", '{"name":"Dana"}'])
    inline_out = capsys.readouterr().out
    assert inline_code == 0
    assert json.loads(inline_out.split("Outputs:\n", 1)[1])["message"] == "Hello Dana"

    file_code = _run_cli(["run", str(workflow_path), "-j", f"@{json_path}"])
    file_out = capsys.readouterr().out
    assert file_code == 0
    assert json.loads(file_out.split("Outputs:\n", 1)[1]) == {"message": "Hello Carol", "count": 5}


def test_cli_run_key_value_overrides_json_input(tmp_path, capsys) -> None:
    path = tmp_path / "workflow.yaml"
    path.write_text(SIMPLE_WORKFLOW, encoding="utf-8")

    code = _run_cli(["run", str(path), "-j", '{"name":"Eve","count":2}', "-i", "name=Frank"])

    out = capsys.readouterr().out
    assert code == 0
    assert json.loads(out.split("Outputs:\n", 1)[1]) == {"message": "Hello Frank", "count": 2}


def test_cli_run_invalid_input_json_returns_error(tmp_path, capsys) -> None:
    path = tmp_path / "workflow.yaml"
    path.write_text(SIMPLE_WORKFLOW, encoding="utf-8")

    code = _run_cli(["run", str(path), "-j", "[]"])

    assert code == 1
    assert "Input error" in capsys.readouterr().out
