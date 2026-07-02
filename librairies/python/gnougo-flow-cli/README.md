# gnougo-flow-cli

CLI Python de démonstration pour lancer, valider et inspecter des workflows GnOuGo.Flow avec `gnougo-flow-core`.

## Installation (uv)

```bash
uv sync --extra dev
```

## Commandes

```bash
uv run gnougo-flow-cli --help
uv run gnougo-flow-cli validate examples/basic.yaml
uv run gnougo-flow-cli inspect examples/basic.yaml
uv run gnougo-flow-cli run examples/basic.yaml -i name=World
uv run gnougo-flow-cli run examples/basic.yaml -j '{"name":"World"}'
uv run gnougo-flow-cli run examples/basic.yaml -j @inputs.json
uv run gnougo-flow-cli examples list
uv run gnougo-flow-cli examples show basic
uv run gnougo-flow-cli run examples/dynamic-workflow-agent.yaml -i "task=Write a short haiku about cloud security"
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
uv run gnougo-flow-cli run examples/basic.yaml -i name=World --llm auto
uv run gnougo-flow-cli run examples/basic.yaml -i name=World --llm openai
uv run gnougo-flow-cli run examples/basic.yaml -i name=World --llm stub
```

- `auto`: OpenAI si clé dispo, sinon stub local
- `openai`: exige une clé API
- `stub`: mode démo hors-ligne

Choix du backend MCP:

```bash
uv run gnougo-flow-cli run examples/mcp-discovery.yaml --mcp auto
uv run gnougo-flow-cli run examples/mcp-discovery.yaml --mcp real
uv run gnougo-flow-cli run examples/mcp-discovery.yaml --mcp stub
uv run gnougo-flow-cli run examples/mcp-discovery-real.yaml --mcp real --llm stub
```

- `auto`: utilise un vrai client MCP stdio si des serveurs sont configurés, sinon stub.
- `real`: impose le client MCP stdio réel.
- `stub`: force le serveur MCP de démo local.

Le CLI charge automatiquement les mêmes configurations MCP que .NET depuis:

- `src/GnOuGo.Flow.Cli/appsettings.json`
- `src/GnOuGo.Flow.Server/appsettings.json`

Les chemins `--project` relatifs sont résolus vers la racine du workspace.

MCP capability discovery is cached for one hour by default. Configure the sliding
expiration with the same appsettings-style section used by the .NET host:

```json
{
  "McpCapabilityCache": {
    "SlidingExpirationSeconds": 3600
  }
}
```

## OpenTelemetry

Vous pouvez exporter les spans via OTLP HTTP:

```bash
uv run gnougo-flow-cli run examples/basic.yaml -i name=World --otlp-endpoint http://localhost:4318/v1/traces
```

Configuration par defaut via `settings.example.json` (export désactivé par défaut):

```json
{
  "telemetry": {
	"enabled": false,
	"service_name": "gnougo-flow-cli",
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


Workflow root spans include the workflow source for debugging. The emitted attributes are
`gnougo-flow.workflow.source`, `gnougo-flow.workflow.source.format`,
`gnougo-flow.workflow.source.length`, `gnougo-flow.workflow.source.truncated`, and
`gnougo-flow.workflow.source.limit`. The source attribute is truncated at 64 KiB.

## Notes de parite

- Le CLI utilise `gnougo-flow-core`; il peut brancher un LLM OpenAI réel ou un stub.
- `run` accepte les entrées Phase 7: `-i key=value` répétable et `-j JSON|@path.json`; `--inputs` reste disponible pour compatibilité.
- `--run-id` active les sauvegardes de checkpoint en mémoire pendant l'exécution de démonstration.
- `workflow.plan` reçoit maintenant un contexte enrichi (types d'étapes + documentation MCP découverte) et peut utiliser les patterns MCP direct ou LLM-assisted.
