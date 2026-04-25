from gnougo_flow_cli.settings import load_settings


def test_load_settings_imports_dotnet_mcp_servers() -> None:
    settings = load_settings(None)
    assert settings.workspace_root is not None
    # Imported from src/GnOuGo.Flow.Cli/appsettings.json or src/GnOuGo.Flow.Server/appsettings.json
    assert settings.mcp_servers
    assert any(name.startswith("GnOuGo.") for name in settings.mcp_servers.keys())


def test_load_settings_has_default_telemetry_from_settings_example() -> None:
    settings = load_settings(None)
    assert settings.telemetry.enabled is False
    assert settings.telemetry.service_name == "gnougo-flow-cli"
    assert settings.telemetry.otlp_endpoint == "http://localhost:4318/v1/traces"


