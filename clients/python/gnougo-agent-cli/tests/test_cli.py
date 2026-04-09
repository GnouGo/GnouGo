from pathlib import Path

from typer.testing import CliRunner

from gnougo_agent_cli.cli import app


runner = CliRunner()


def test_examples_list_command() -> None:
    result = runner.invoke(app, ["examples", "list"])
    assert result.exit_code == 0
    assert "basic" in result.stdout


def test_validate_example() -> None:
    base = Path(__file__).resolve().parents[1]
    workflow = base / "examples" / "basic.yaml"
    result = runner.invoke(app, ["validate", str(workflow)])
    assert result.exit_code == 0
    assert "Validation OK" in result.stdout


def test_run_example() -> None:
    base = Path(__file__).resolve().parents[1]
    workflow = base / "examples" / "basic.yaml"
    result = runner.invoke(app, ["run", str(workflow), "--inputs", '{"name":"World"}'])
    assert result.exit_code == 0
    assert '"success": true' in result.stdout

