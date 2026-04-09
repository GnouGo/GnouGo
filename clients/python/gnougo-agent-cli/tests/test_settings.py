from gnougo_agent_cli.settings import load_settings


def test_load_settings_imports_dotnet_mcp_servers() -> None:
    settings = load_settings(None)
    assert settings.workspace_root is not None
    # Imported from src/GnOuGo.Flow.Cli/appsettings.json or src/GnOuGo.Flow.Server/appsettings.json
    assert settings.mcp_servers
    assert any(name.startswith("GnOuGo.") for name in settings.mcp_servers.keys())

