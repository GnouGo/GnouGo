# GnOuGo.AI.Core

Low-level, AOT-friendly library for calling LLM providers.  
Provides a **provider-agnostic routing layer** so that the rest of the system never deals with HTTP specifics.

## Supported Providers

| Provider key | Type      | Description                                      |
|-------------|-----------|--------------------------------------------------|
| `OpenAi`    | `openai`  | OpenAI, Azure OpenAI, or any compatible endpoint |
| `Ollama`    | `ollama`  | Local Ollama server                              |
| `Copilot`   | `copilot` | GitHub Copilot / GitHub Models API               |

## Model Catalog Behavior

`ILLMModelCatalog` returns the provider-discovered catalog enriched with GnOuGo model metadata.

- OpenAI-compatible providers and Copilot/GitHub Models return the advertised catalog directly.
- GnOuGo does not run extra chat-completions probes during model listing.
- OIDC client-credentials authentication is supported for both inference calls and model discovery.
- The embedded metadata catalog adds pricing, token limits and request capabilities when known.
- User metadata files and inline overrides can add new models or override builtin values without recompilation.
- `RoutingLLMClient` removes unsupported optional fields (for example `temperature` on reasoning models) before calling the provider.
- Builtin metadata is authored in `Telemetry/model-metadata.json`; pricing is stored under each model's `pricing` object.
- Builtin and external metadata can use provider-qualified keys such as `openai/gpt-4o`, `copilot/gpt-4o`, `claude/claude-sonnet-4-20250514`, or `ollama/llama3.1` when the same model id exists on multiple providers with different limits or pricing.
- `scripts/update-model-metadata.ps1 -DownloadLatest` and `scripts/update-model-metadata.sh --download-latest` synchronize the builtin catalog from LiteLLM for the supported providers (`openai`, `claude`/`anthropic`, `copilot`/GitHub Models, and `ollama`) and regenerate `ModelMetadataCatalog.Generated.cs`.

Metadata precedence is:

```text
embedded catalog < LLM.ModelMetadataFiles < LLM.ModelOverrides < provider/model heuristics for missing values
```

## Architecture

```
ILLMProvider  (interface — one per backend)
  ├── OpenAiLLMProvider
  ├── OllamaLLMProvider
  └── CopilotLLMProvider

RoutingLLMClient  (routes requests to the right ILLMProvider based on config)
```

### Adding a new provider

1. Create a class implementing `ILLMProvider` in this project.
2. Register it in `RoutingLLMClient.CreateDefaultProviders()` (or pass it via the `IEnumerable<ILLMProvider>` constructor).
3. Add a matching entry in the `LLM.Models` configuration section.

## Configuration (appsettings.json)

```jsonc
{
  "LLM": {
    "DefaultProvider": "Copilot",
    "DefaultModel": "gpt-4.1",
    "Models": {
      "OpenAi": {
        "Url": "https://api.openai.com/v1",
        "ApiKey": "sk-..."             // or set OPENAI_API_KEY env var
      },
      "Ollama": {
        "Url": "http://localhost:11434",
        "Type": "ollama"
      },
      "Copilot": {
        "Url": "https://models.github.ai/inference",
        "Type": "copilot",
        "ApiKey": null                  // or set GITHUB_TOKEN env var
      }
    },
    "ModelMetadataFiles": [
      "config/my-models.json"
    ],
    "ModelOverrides": {
      "o4-mini": {
        "maxOutputTokens": 100000,
        "capabilities": {
          "supportsTemperature": false,
          "supportsReasoningEffort": true,
          "unsupportedRequestParameters": ["temperature"]
        }
      },
      "my-local-model:latest": {
        "providerType": "ollama",
        "contextWindowTokens": 32768,
        "maxOutputTokens": 8192,
        "pricing": {
          "currency": "USD",
          "inputPer1MTokens": 0,
          "outputPer1MTokens": 0
        },
        "capabilities": {
          "supportsTemperature": true,
          "supportsReasoningEffort": false,
          "supportsStructuredOutput": false,
          "supportsTools": false
        }
      }
    }
  }
}
```

External metadata files use this shape:

```jsonc
{
  "models": {
      "openai/model-id": {
      "providerType": "openai",
      "displayName": "Model name",
      "contextWindowTokens": 128000,
      "maxInputTokens": 128000,
      "maxOutputTokens": 16384,
      "pricing": {
        "currency": "USD",
        "inputPer1MTokens": 0.15,
        "outputPer1MTokens": 0.60
      },
      "capabilities": {
        "supportsTemperature": true,
        "supportsReasoningEffort": false,
        "supportsStructuredOutput": true,
        "supportsTools": true,
        "unsupportedRequestParameters": []
      }
    }
  },
  "aliases": {
    "short-name": "openai/model-id",
    "copilot/short-name": "copilot/model-id"
  }
}
```

Provider-qualified keys are preferred whenever the same model id can appear under different providers with different costs.

### Copilot / GitHub Models

The Copilot provider connects to the [GitHub Models](https://github.com/marketplace/models) inference endpoint, which is OpenAI-compatible.

**Authentication** (in priority order):
1. `ApiKey` in the configuration
2. `GITHUB_TOKEN` environment variable
3. `COPILOT_API_KEY` environment variable

If `Issuer`, `ClientId`, `Scopes`, and `ClientSecret` are configured, GnOuGo first obtains an OIDC access token and uses it for both chat inference and model discovery.

**Model names** can use the vendor prefix format (`openai/gpt-4.1`, `anthropic/claude-sonnet-4`) — the prefix is automatically stripped before sending to the API. Plain names like `gpt-4.1` or `o4-mini` also work.

### Claude / Anthropic

The Claude provider connects to the Anthropic Messages API. Configure it with provider type `claude` (the alias `anthropic` is also accepted) and endpoint `https://api.anthropic.com/v1`.

**Authentication** (in priority order):
1. `ApiKey` in the provider configuration, sent as `x-api-key`
2. `ANTHROPIC_API_KEY` environment variable
3. `CLAUDE_API_KEY` environment variable
4. OIDC client credentials, sent as a bearer token when `Issuer`, `ClientId`, and `Scopes` are configured

Claude supports text responses, tool use (`tool_use` blocks), live model discovery via `/v1/models`, and best-effort structured JSON output by appending a strict JSON instruction to the prompt.

## Reasoning / Thinking effort

`LLMClientRequest.Reasoning` (and `LLMRequest.Reasoning` in `GnOuGo.Flow.Core`) controls the
"thinking" / reasoning effort of capable models without hard-coding any provider-specific field.

Accepted values: `"minimal" | "low" | "medium" | "high" | "max" | "auto"` (or `null`).

| Value           | OpenAI / Copilot (GitHub Models)        | Ollama                | Claude / Anthropic         |
|-----------------|-----------------------------------------|-----------------------|----------------------------|
| `null` / `auto` | field omitted (provider default)        | field omitted         | field omitted              |
| `minimal`       | `reasoning_effort: "minimal"`           | `think: true`         | `thinking.budget_tokens=1024` |
| `low`           | `reasoning_effort: "low"`               | `think: true`         | `thinking.budget_tokens=1024` |
| `medium`        | `reasoning_effort: "medium"`            | `think: true`         | `thinking.budget_tokens=4096` |
| `high` / `max`  | `reasoning_effort: "high"`              | `think: true`         | `thinking.budget_tokens=8192/16000` |
| `none` / `off`  | (treated as `auto`)                     | `think: false`        | field omitted              |

Models that don't support thinking have the field removed by `LLMRequestSanitizer` before the provider call.
Provider-specific mapping lives in `ChatRequestBuilder.NormalizeOpenAiReasoning`, `NormalizeOllamaThink`, and `ClaudeLLMProvider.NormalizeThinkingBudget`.

## Build

```bash
dotnet build src/GnOuGo.AI.Core/GnOuGo.AI.Core.csproj
```

## Test

```bash
dotnet test tests/GnOuGo.AI.Core.Tests/GnOuGo.AI.Core.Tests.csproj
```

## Key Types

| Type | Role |
|------|------|
| `ILLMProvider` | Interface — implement to add a new backend |
| `RoutingLLMClient` | Routes `LLMClientRequest` to the correct provider |
| `LLMOptions` / `ModelProviderOptions` | Configuration model |
| `LLMModelMetadataResolver` | Merges builtin metadata, files and inline overrides |
| `LLMRequestSanitizer` | Removes unsupported optional request parameters |
| `ChatRequestBuilder` | AOT-friendly JSON request builder |
| `ChatResponseParser` | Response parser for all providers |
| `CopilotEndpoints` / `OpenAiEndpoints` / `OllamaEndpoints` | URL helpers |

