from gnougo_flow_cli.settings import FlowCliSettings, load_settings


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


def test_mcp_capability_cache_defaults_to_one_hour() -> None:
    settings = FlowCliSettings.model_validate({})
    assert settings.mcp_capability_cache.ttl_seconds == 3600


def test_mcp_capability_cache_accepts_dotnet_style_alias() -> None:
    settings = FlowCliSettings.model_validate(
        {"McpCapabilityCache": {"SlidingExpirationSeconds": 42}}
    )
    assert settings.mcp_capability_cache.ttl_seconds == 42

