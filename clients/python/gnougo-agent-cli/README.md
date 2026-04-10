# gnougo-agent-cli

CLI Python pour lancer, valider et inspecter des workflows GnOuGo.Flow.

## Installation (uv)

```bash
uv sync --extra dev
```

## Commandes

```bash
uv run gnougo-agent --help
uv run gnougo-agent validate examples/basic.yaml
uv run gnougo-agent inspect examples/basic.yaml
uv run gnougo-agent run examples/basic.yaml --inputs '{"name":"World"}'
uv run gnougo-agent examples list
uv run gnougo-agent examples show basic
uv run gnougo-agent run examples/dynamic-workflow-agent.yaml --inputs '{"task":"Write a short haiku about cloud security"}'
```

## Configuration (axa-fr-app-settings)

Le CLI charge la configuration via `axa-fr-app-settings` depuis:

- `settings.json` / `settings.yaml` (si présents)
- `.env`
- variables d'environnement `GNOUGO__*`

Exemple `settings.json`:

```json
{
  "openai": {
	"api_key": "sk-...",
	"model": "gpt-4o-mini",
	"base_url": null,
	"organization": null,
	"timeout_seconds": 60
  }
}
```

Exemple variables d'environnement:

```bash
export GNOUGO__OPENAI__API_KEY="sk-..."
export GNOUGO__OPENAI__MODEL="gpt-4o-mini"
```

Choix du backend LLM:

```bash
uv run gnougo-agent run examples/basic.yaml --inputs '{"name":"World"}' --llm auto
uv run gnougo-agent run examples/basic.yaml --inputs '{"name":"World"}' --llm openai
uv run gnougo-agent run examples/basic.yaml --inputs '{"name":"World"}' --llm stub
```

- `auto`: OpenAI si clé dispo, sinon stub local
- `openai`: exige une clé API
- `stub`: mode démo hors-ligne

Choix du backend MCP:

```bash
uv run gnougo-agent run examples/mcp-discovery.yaml --mcp auto
uv run gnougo-agent run examples/mcp-discovery.yaml --mcp real
uv run gnougo-agent run examples/mcp-discovery.yaml --mcp stub
uv run gnougo-agent run examples/mcp-discovery-real.yaml --mcp real --llm stub
```

- `auto`: utilise un vrai client MCP stdio si des serveurs sont configurés, sinon stub.
- `real`: impose le client MCP stdio réel.
- `stub`: force le serveur MCP de démo local.

Le CLI charge automatiquement les mêmes configurations MCP que .NET depuis:

- `src/GnOuGo.Flow.Cli/appsettings.json`
- `src/GnOuGo.Flow.Server/appsettings.json`

Les chemins `--project` relatifs sont résolus vers la racine du workspace.

## OpenTelemetry

Vous pouvez exporter les spans via OTLP HTTP:

```bash
uv run gnougo-agent run examples/basic.yaml --inputs '{"name":"World"}' --otlp-endpoint http://localhost:4318/v1/traces
```

Configuration par defaut via `settings.example.json`:

```json
{
  "telemetry": {
	"enabled": true,
	"service_name": "gnougo-agent-cli",
	"otlp_endpoint": "http://localhost:4318/v1/traces",
	"protocol": "http/protobuf",
	"tenant_id": ""
  }
}
```

Priorite de resolution:

- `--otlp-endpoint` (option CLI)
- `telemetry.otlp_endpoint` dans les settings
- sinon, pas d'export OTLP

## Notes de parite

- Le CLI utilise `gnougo-flow-core`; il peut brancher un LLM OpenAI réel ou un stub.
- `workflow.plan` reçoit maintenant un contexte enrichi (types d'étapes + documentation MCP découverte), mais la parité n'est pas strictement byte-à-byte avec l'implémentation .NET.

