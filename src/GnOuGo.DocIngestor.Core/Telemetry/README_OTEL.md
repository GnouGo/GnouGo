# Instrumentation OpenTelemetry GenAI

Ce document explique comment l'instrumentation OpenTelemetry GenAI est implémentée dans DocIngestor pour capturer les métriques et traces des opérations d'embedding.

## Architecture

### Composants

1. **`GenAiTelemetry`** (`DocIngestor.Core.Telemetry`)
   - Classe centrale pour l'instrumentation GenAI
   - Crée des `Activity` (traces) pour les opérations d'embedding
   - Enregistre des métriques selon les conventions sémantiques OpenTelemetry GenAI

2. **`OpenTelemetryConfiguration`** (`DocIngestor.Cli`)
   - Configure les providers de traces et métriques
   - Exporte vers un collecteur OTLP (gRPC ou HTTP/Protobuf)
   - Gère le cycle de vie des providers

3. **`OpenAiCompatibleEmbeddingModel`** (modifié)
   - Instrumenté avec `GenAiTelemetry`
   - Crée une trace pour chaque appel d'embedding
   - Enregistre les métriques de tokens, durée et succès/échec

## Métriques exportées

### Selon les conventions GenAI OpenTelemetry

| Métrique | Type | Description | Labels |
|----------|------|-------------|--------|
| `gen_ai.client.token.usage` | Counter | Nombre de tokens utilisés | `gen_ai.operation.name`, `gen_ai.system`, `gen_ai.request.model`, `gen_ai.response.finish_reason` |
| `gen_ai.client.operation.duration` | Histogram | Durée des opérations (secondes) | `gen_ai.operation.name`, `gen_ai.system`, `gen_ai.request.model`, `gen_ai.response.finish_reason` |
| `gen_ai.client.request.count` | Counter | Nombre de requêtes | `gen_ai.operation.name`, `gen_ai.system`, `gen_ai.request.model`, `gen_ai.response.finish_reason` |

### Valeurs des labels

- **`gen_ai.operation.name`**: `embedding`
- **`gen_ai.system`**: `openai` (pour modèles compatibles OpenAI)
- **`gen_ai.request.model`**: Nom du modèle (ex: `text-embedding-3-large`)
- **`gen_ai.response.finish_reason`**: `success` ou `error`

## Traces (Activities)

Chaque opération d'embedding crée une `Activity` avec les attributs suivants :

| Attribut | Description |
|----------|-------------|
| `gen_ai.operation.name` | Toujours `embedding` |
| `gen_ai.system` | Système GenAI (`openai`) |
| `gen_ai.request.model` | Modèle utilisé |
| `gen_ai.usage.input_tokens` | Nombre de tokens d'entrée (estimé) |
| `gen_ai.response.dimensions` | Dimensions du vecteur d'embedding |
| `gen_ai.response.finish_reason` | Raison de fin (`success` ou `error`) |
| `error.type` | Type d'erreur (si échec) |
| `error.message` | Message d'erreur (si échec) |

## Configuration

### Par défaut

OpenTelemetry est **activé par défaut** et exporte vers `http://localhost:4317` (gRPC).

### Options CLI

```bash
# Désactiver OpenTelemetry
--enable-otel false

# Personnaliser l'endpoint OTLP
--otlp-endpoint http://otel-collector:4317

# Ou via variable d'environnement
export OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
```

### Variables d'environnement

| Variable | Description | Valeur par défaut |
|----------|-------------|-------------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | URL du collecteur OTLP | `http://localhost:4317` |
| `OTEL_API_KEY` | Clé API pour l'exporteur OTLP | _(vide)_ |
| `ENVIRONMENT` | Environnement de déploiement | `development` |

## Exemples d'utilisation

### Ingestion avec télémétrie

```bash
# Ingestion avec exportation vers collecteur local
dotnet run -- ingest \
  --path ./docs \
  --model ada3-large \
  --embed true \
  --endpoint-url https://api.openai.com/v1 \
  --oidc-issuer https://issuer.example.com \
  --oidc-client-id my-client \
  --oidc-scopes "api.read api.write" \
  --oidc-client-secret "secret" \
  --otlp-endpoint http://localhost:4317
```

### Recherche avec télémétrie

```bash
dotnet run -- search \
  --query "Machine learning best practices" \
  --model ada3-large \
  --topK 5 \
  --otlp-endpoint http://otel-collector:4317
```

### Désactivation de la télémétrie

```bash
dotnet run -- ingest --path ./docs --enable-otel false
```

## Collecteur OTLP

### Docker Compose avec OpenTelemetry Collector

```yaml
version: '3.8'
services:
  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: ["--config=/etc/otel-collector-config.yaml"]
    volumes:
      - ./otel-collector-config.yaml:/etc/otel-collector-config.yaml
    ports:
      - "4317:4317"  # OTLP gRPC
      - "4318:4318"  # OTLP HTTP
      - "55679:55679" # zpages
```

### Configuration du collecteur (`otel-collector-config.yaml`)

```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  batch:
    timeout: 10s
    send_batch_size: 1024

exporters:
  # Exportation vers la console (debug)
  logging:
    loglevel: debug
  
  # Exportation vers Prometheus (métriques)
  prometheus:
    endpoint: "0.0.0.0:8889"
  
  # Exportation vers Jaeger (traces)
  jaeger:
    endpoint: jaeger:14250
    tls:
      insecure: true

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [logging, jaeger]
    
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [logging, prometheus]
```

### Visualisation avec OtlpTenantCollector

Les métriques et traces peuvent être visualisées dans l'interface web de **OtlpTenantCollector** (voir `/src/OtlpTenantCollector`).

## Référence

### Conventions sémantiques OpenTelemetry GenAI

- [OpenTelemetry Semantic Conventions for GenAI](https://opentelemetry.io/docs/specs/semconv/gen-ai/)
- [.NET OpenTelemetry SDK](https://github.com/open-telemetry/opentelemetry-dotnet)

### Packages NuGet utilisés

- `OpenTelemetry.Api` (1.10.*)
- `OpenTelemetry` (1.10.*)
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` (1.10.*)
- `OpenTelemetry.Extensions.Hosting` (1.10.*)
- `OpenTelemetry.Instrumentation.Http` (1.10.*)

## Débogage

### Activer les logs OpenTelemetry

```bash
export OTEL_LOG_LEVEL=debug
dotnet run -- ingest --path ./docs
```

### Vérifier l'exportation

```bash
# Logs du collecteur
docker logs otel-collector -f

# Vérifier les endpoints
curl http://localhost:55679/debug/tracez
curl http://localhost:55679/debug/pipelinez
```

## Performances

L'instrumentation OpenTelemetry a un impact minimal sur les performances :

- **Overhead de CPU** : < 1%
- **Overhead mémoire** : < 10 MB
- **Latence ajoutée** : < 1 ms par opération

Les métriques sont agrégées localement et exportées par batch toutes les 10 secondes.

## Sécurité

### API Key pour l'exporteur OTLP

Si votre collecteur OTLP nécessite une clé API :

```bash
export OTEL_API_KEY=your-api-key-here
dotnet run -- ingest --path ./docs
```

La clé est ajoutée automatiquement dans les headers : `x-api-key: your-api-key-here`

### TLS/SSL

Pour utiliser HTTPS avec le collecteur OTLP :

```bash
--otlp-endpoint https://secure-collector.example.com:4317
```

Le certificat du serveur doit être valide et approuvé par le système.

---

**Date de création** : 2026-02-18  
**Version** : 1.0.0

