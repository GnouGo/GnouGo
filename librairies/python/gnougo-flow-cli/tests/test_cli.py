import json
from pathlib import Path

import pytest
from click import unstyle
from typer.testing import CliRunner

from gnougo_flow_cli.cli import _resolve_telemetry_config, app
from gnougo_flow_cli.settings import FlowCliSettings

runner = CliRunner()


@pytest.mark.parametrize("ci_terminal", [False, True])
def test_durable_run_command_requires_revision_before_dispatch(monkeypatch, ci_terminal) -> None:
    if ci_terminal:
        for name, value in {"GITHUB_ACTIONS": "true", "CI": "true", "TERM": "xterm-256color",
                            "FORCE_COLOR": "1"}.items():
            monkeypatch.setenv(name, value)
    calls = []

    async def dispatch(*args, **kwargs):
        calls.append((args, kwargs))
        raise AssertionError("A missing revision must prevent dispatch")

    monkeypatch.setattr("gnougo_flow_cli.cli.WorkflowRunClient.command_async", dispatch)
    result = runner.invoke(app, ["runs", "--server", "http://127.0.0.1:1", "--tenant", "a",
                                 "--id", "run", "--command", "resume"])
    assert result.exit_code == 2
    assert "--revision" in unstyle(result.output)
    assert not calls


def test_durable_run_command_forwards_tenant_and_revision(monkeypatch) -> None:
    calls = []

    class Client:
        def __init__(self, server, tenant):
            calls.append((server, tenant))

        async def command_async(self, run_id, command, revision, **kwargs):
            calls.append((run_id, command, revision, kwargs))
            return {"schemaVersion": 9, "revision": 8, "status": "needs_reconciliation"}

    monkeypatch.setattr("gnougo_flow_cli.cli.WorkflowRunClient", Client)
    result = runner.invoke(app, ["runs", "--server", "http://localhost:5000", "--tenant", "a",
                                 "--id", "run", "--command", "resume", "--revision", "7"])
    assert result.exit_code == 0
    assert json.loads(result.stdout)["status"] == "needs_reconciliation"
    assert calls == [("http://localhost:5000", "a"), ("run", "resume", 7,
                     {"invocation_id": None, "confirmed_stopped_reason": None})]


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
    result = runner.invoke(app, ["run", str(workflow), "-i", "name=World", "--json"])
    assert result.exit_code == 0
    assert '"success": true' in result.stdout


def test_run_example_accepts_input_json_file(tmp_path) -> None:
    base = Path(__file__).resolve().parents[1]
    workflow = base / "examples" / "basic.yaml"
    inputs = tmp_path / "inputs.json"
    inputs.write_text('{"name":"JsonFile"}', encoding="utf-8")

    result = runner.invoke(app, ["run", str(workflow), "-j", f"@{inputs}", "--json"])

    assert result.exit_code == 0
    assert '"success": true' in result.stdout
    assert "JsonFile" in result.stdout


def test_run_example_prints_dotnet_like_summary() -> None:
    base = Path(__file__).resolve().parents[1]
    workflow = base / "examples" / "basic.yaml"

    result = runner.invoke(app, ["run", str(workflow), "-i", "name=World"])

    assert result.exit_code == 0
    assert "Running workflow" in result.stdout
    assert "Workflow completed successfully" in result.stdout
    assert "Steps executed:" in result.stdout
    assert "Outputs:" in result.stdout


def test_resolve_telemetry_config_uses_settings_default_when_no_cli_override() -> None:
    settings = FlowCliSettings.model_validate(
        {
            "telemetry": {
                "enabled": True,
                "service_name": "from-settings",
                "otlp_endpoint": "http://localhost:4318/v1/traces",
            }
        }
    )

    config = _resolve_telemetry_config(settings, None)
    assert config.service_name == "from-settings"
    assert config.otlp_endpoint == "http://localhost:4318/v1/traces"


def test_resolve_telemetry_config_prefers_cli_override() -> None:
    settings = FlowCliSettings.model_validate(
        {
            "telemetry": {
                "enabled": True,
                "service_name": "from-settings",
                "otlp_endpoint": "http://localhost:4318/v1/traces",
            }
        }
    )

    config = _resolve_telemetry_config(settings, "http://collector:4318/v1/traces")
    assert config.otlp_endpoint == "http://collector:4318/v1/traces"


def test_resolve_telemetry_config_disables_export_when_telemetry_is_disabled() -> None:
    settings = FlowCliSettings.model_validate(
        {
            "telemetry": {
                "enabled": False,
                "service_name": "from-settings",
                "otlp_endpoint": "http://localhost:4318/v1/traces",
            }
        }
    )

    config = _resolve_telemetry_config(settings, None)
    assert config.service_name == "from-settings"
    assert config.otlp_endpoint is None
