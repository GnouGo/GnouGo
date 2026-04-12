from pathlib import Path

from typer.testing import CliRunner

from gnougo_agent_cli.cli import _resolve_telemetry_config, app
from gnougo_agent_cli.settings import AgentCliSettings


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


def test_resolve_telemetry_config_uses_settings_default_when_no_cli_override() -> None:
    settings = AgentCliSettings.model_validate(
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
    settings = AgentCliSettings.model_validate(
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
    settings = AgentCliSettings.model_validate(
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


