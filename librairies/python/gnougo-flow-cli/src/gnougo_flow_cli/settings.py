from __future__ import annotations

from pathlib import Path

from axa_fr_app_settings import SettingsBuilder, SettingsModel
from pydantic import AliasChoices, Field, model_validator


class OpenAiSettings(SettingsModel):
    api_key: str | None = None
    model: str = "gpt-4o-mini"
    base_url: str | None = None
    organization: str | None = None
    timeout_seconds: float = 60.0


class LlmModelSettings(SettingsModel):
    api_key: str | None = Field(default=None, validation_alias=AliasChoices("api_key", "ApiKey"))
    url: str | None = Field(default=None, validation_alias=AliasChoices("url", "Url"))


class McpServerSettings(SettingsModel):
    type: str = Field(default="stdio", validation_alias=AliasChoices("type", "Type"))
    description: str | None = Field(
        default=None,
        validation_alias=AliasChoices("description", "Description"),
    )
    command: str | None = Field(default=None, validation_alias=AliasChoices("command", "Command"))
    args: list[str] = Field(default_factory=list, validation_alias=AliasChoices("args", "Args"))
    cwd: str | None = Field(default=None, validation_alias=AliasChoices("cwd", "Cwd"))
    env: dict[str, str] = Field(default_factory=dict, validation_alias=AliasChoices("env", "Env"))
    url: str | None = Field(default=None, validation_alias=AliasChoices("url", "Url"))
    api_key: str | None = Field(default=None, validation_alias=AliasChoices("api_key", "ApiKey"))


class LlmSettings(SettingsModel):
    models: dict[str, LlmModelSettings] = Field(
        default_factory=dict,
        validation_alias=AliasChoices("models", "Models"),
    )
    mcp_servers: dict[str, McpServerSettings] = Field(
        default_factory=dict,
        validation_alias=AliasChoices("mcp_servers", "McpServers"),
    )


class TelemetrySettings(SettingsModel):
    enabled: bool = Field(default=False, validation_alias=AliasChoices("enabled", "Enabled"))
    service_name: str = Field(
        default="gnougo-flow-cli",
        validation_alias=AliasChoices("service_name", "ServiceName"),
    )
    otlp_endpoint: str | None = Field(
        default="http://localhost:4318/v1/traces",
        validation_alias=AliasChoices("otlp_endpoint", "OtlpEndpoint", "endpoint", "Endpoint"),
    )
    protocol: str | None = Field(
        default="http/protobuf",
        validation_alias=AliasChoices("protocol", "Protocol"),
    )
    tenant_id: str | None = Field(
        default=None,
        validation_alias=AliasChoices("tenant_id", "TenantId"),
    )


class McpCapabilityCacheSettings(SettingsModel):
    sliding_expiration_seconds: float = Field(
        default=3600.0,
        validation_alias=AliasChoices("sliding_expiration_seconds", "SlidingExpirationSeconds"),
    )

    @property
    def ttl_seconds(self) -> float:
        return max(1.0, float(self.sliding_expiration_seconds))


class FlowCliSettings(SettingsModel):
    openai: OpenAiSettings = Field(default_factory=OpenAiSettings)
    llm: LlmSettings = Field(
        default_factory=LlmSettings,
        validation_alias=AliasChoices("llm", "LLM"),
    )
    telemetry: TelemetrySettings = Field(
        default_factory=TelemetrySettings,
        validation_alias=AliasChoices("telemetry", "Telemetry", "open_telemetry", "OpenTelemetry"),
    )
    mcp_capability_cache: McpCapabilityCacheSettings = Field(
        default_factory=McpCapabilityCacheSettings,
        validation_alias=AliasChoices("mcp_capability_cache", "McpCapabilityCache"),
    )
    mcp_servers: dict[str, McpServerSettings] = Field(default_factory=dict)
    workspace_root: str | None = None

    @model_validator(mode="after")
    def _merge_derived(self) -> "FlowCliSettings":
        # Fill OpenAI settings from .NET-style LLM.Models.OpenAi when not explicitly set.
        if not self.openai.api_key and self.llm.models:
            for key, model in self.llm.models.items():
                if key.lower() == "openai" and model.api_key:
                    self.openai.api_key = model.api_key
                if key.lower() == "openai" and model.url and not self.openai.base_url:
                    self.openai.base_url = model.url

        # Expose MCP servers at top-level for CLI runtime wiring.
        if not self.mcp_servers and self.llm.mcp_servers:
            self.mcp_servers = dict(self.llm.mcp_servers)

        return self


def _find_workspace_root() -> Path:
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / "GnOuGo.Agent.sln").exists():
            return parent
    # Fallback to current project root
    return Path(__file__).resolve().parents[2]


def _normalize_mcp_servers(settings: AgentCliSettings, root: Path) -> None:
    for server in settings.mcp_servers.values():
        if server.cwd is None:
            server.cwd = str(root)
        if server.args and "--project" in server.args:
            idx = server.args.index("--project")
            if idx + 1 < len(server.args):
                proj = Path(server.args[idx + 1])
                if not proj.is_absolute():
                    server.args[idx + 1] = str((root / proj).resolve())


AgentCliSettings = FlowCliSettings


def load_settings(settings_path: Path | None = None) -> FlowCliSettings:
    workspace_root = _find_workspace_root()
    builder = SettingsBuilder(FlowCliSettings)

    if settings_path is not None:
        suffix = settings_path.suffix.lower()
        if suffix in {".yaml", ".yml"}:
            builder.add_yaml_file(str(settings_path), optional=False)
        else:
            builder.add_json_file(str(settings_path), optional=False)

    # Conventional local files (support both legacy typo and correct filename).
    builder.add_json_file("settings.example.json", optional=True)
    builder.add_json_file("settings.exemple.json", optional=True)

    # Environment sources (prefix + nested format: GNOUGO__OPENAI__API_KEY)
    builder.add_env_file(path=".env", optional=True, prefix="GNOUGO__", nested_delimiter="__")
    builder.add_environment_variables(prefix="GNOUGO__", nested_delimiter="__")

    settings = builder.build()
    settings.workspace_root = str(workspace_root)
    _normalize_mcp_servers(settings, workspace_root)
    return settings
