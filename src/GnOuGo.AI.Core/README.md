# GnOuGo.AI.Core

<a href="https://www.nuget.org/packages/GnOuGo.AI.Core"><img src="https://img.shields.io/nuget/v/GnOuGo.AI.Core.svg" alt="NuGet version"></a>
<a href="https://www.nuget.org/packages/GnOuGo.AI.Core"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt=".NET 10.0"></a>
<a href="https://nugettrends.com/packages?ids=GnOuGo.AI.Core"><img src="https://img.shields.io/nuget/dt/GnOuGo.AI.Core.svg" alt="NuGet downloads"></a>

Low-level, AOT-friendly library for calling LLM providers.  
Provides a **provider-agnostic routing layer** so that the rest of the system never deals with HTTP specifics.

## Supported Providers

| Provider key | Type      | Description                                      |
|-------------|-----------|--------------------------------------------------|
| `OpenAi`    | `openai`  | OpenAI, Azure OpenAI, or any compatible endpoint |
| `Ollama`    | `ollama`  | Local Ollama server                              |
| `Copilot`   | `copilot` | GitHub Copilot / GitHub Models API               |
| `Anthropic` | `anthropic` | Anthropic Claude Messages API                  |

## Model Catalog Behavior

`ILLMModelCatalog` returns the provider-discovered catalog enriched with GnOuGo model metadata.

- OpenAI-compatible providers and Copilot/GitHub Models return the advertised catalog directly.
- GnOuGo does not run extra chat-completions probes during model listing.
- OIDC client-credentials authentication is supported for both inference calls and model discovery.
- The embedded metadata catalog adds pricing, token limits and request capabilities when known.
- User metadata files and inline overrides can add new models or override builtin values without recompilation.
- Unknown model ids use deterministic, provider-aware fuzzy matching to inherit the closest same-provider limits, pricing and capabilities. Cross-provider fallback is never used.
- `LLMModelMetadataResolver.ResolveWithDetails(...)` exposes whether resolution was exact, alias-based, fuzzy, or heuristic-only, together with the matched model and similarity score. Fuzzy results retain the requested model identity.
- `RoutingLLMClient` removes unsupported optional fields (for example `temperature` on reasoning models) before calling the provider.
- Builtin metadata is authored in `Telemetry/model-metadata.json`; pricing is stored under each model's `pricing` object.
- Builtin and external metadata can use provider-qualified keys such as `openai/gpt-4o`, `copilot/gpt-4o`, `claude/claude-sonnet-4-20250514`, or `ollama/llama3.1` when the same model id exists on multiple providers with different limits or pricing.
- `scripts/update-model-metadata.ps1 -DownloadLatest` and `scripts/update-model-metadata.sh --download-latest` synchronize the builtin catalog from LiteLLM for the supported providers (`openai`, `anthropic`/`claude`, `copilot`/GitHub Models, and `ollama`) and regenerate `ModelMetadataCatalog.Generated.cs`.

Resolution order is:

```text
exact id/alias -> closest normalized model id for the same provider -> provider/model heuristics
```

Within an exact entry or fuzzy candidate, metadata precedence remains:

```text
embedded catalog < LLM.ModelMetadataFiles < LLM.ModelOverrides
```

Because fuzzy metadata also feeds request sanitization and cost estimation, callers that display estimates should use `ResolveWithDetails(...)` when they need to label approximate values.

## Architecture

```
ILLMProvider  (interface — one per backend)
  ├── OpenAiLLMProvider
  ├── OllamaLLMProvider
  ├── CopilotLLMProvider
  └── AnthropicLLMProvider

RoutingLLMClient  (routes requests to the right ILLMProvider based on config)
```

## HTTP resilience

OpenAI, Ollama, Copilot/GitHub Models, and Anthropic HTTP operations retry transient
HTTP `500`–`599` responses up to three times after the initial request. Retries use
exponential backoff delays of 250 ms, 500 ms, and 1,000 ms, recreate the request (including
its payload and authentication headers), honor cancellation, and emit a warning
log for each retry. Terminal server failures and transport timeouts log the attempt
count, attempt duration, and total elapsed request time before the provider raises
the corresponding exception. Non-server HTTP responses such as `400`, `401`,
`404`, and `429` are returned immediately to the provider's normal error handling.

### OpenAI background Responses

`LLMClientRequest.UseBackgroundMode=true` routes OpenAI calls through the Responses API with
`background: true`. Structured-output requests keep their normalized JSON Schema, name, and
strictness under `text.format`; reasoning effort and `max_output_tokens` are preserved as well.
GnOuGo polls only responses whose status is `queued` or `in_progress`, returns `completed`
responses, and surfaces terminal or unexpected statuses without silently switching protocols.

The official `api.openai.com` endpoint never falls back from Responses to Chat Completions.
OpenAI-compatible third-party endpoints fall back only for `405`, `501`, an explicit missing
`/responses` route reported with `404`, or a `400` that explicitly says Responses/background
mode is unsupported. Confirmed endpoint incompatibility is cached for the existing bounded
cache duration; model, authentication, permission, quota, rate-limit, and malformed-request
errors are never cached as endpoint incompatibility. Provider exceptions preserve the HTTP
status without logging credentials or request payloads.

An internal `HttpClient` timeout is exposed as `TimeoutException`; cancellation requested by
the caller remains `OperationCanceledException`.

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

If `Issuer`, `ClientId`, and `Scopes` are configured, GnOuGo first obtains an OIDC access token and uses it for both chat inference and model discovery. You can authenticate with either `ClientSecret` or `PrivateKeyPem` (`private_key_pem` in KeyVault-backed JSON configuration).

**Model names** can use the vendor prefix format (`openai/gpt-4.1`, `anthropic/claude-sonnet-4`) — the prefix is automatically stripped before sending to the API. Plain names like `gpt-4.1` or `o4-mini` also work.

### Anthropic / Claude

The Anthropic provider connects to the Anthropic Messages API. Configure it with provider type `anthropic` and endpoint `https://api.anthropic.com/v1`. The legacy provider type `claude` is still accepted as an alias.

**Authentication** (in priority order):
1. `ApiKey` in the provider configuration, sent as `x-api-key`
2. `ANTHROPIC_API_KEY` environment variable
3. `CLAUDE_API_KEY` environment variable
4. OIDC client credentials, sent as a bearer token when `Issuer`, `ClientId`, and `Scopes` are configured with either `ClientSecret` or `PrivateKeyPem`

Anthropic supports text responses, tool use (`tool_use` blocks), live model discovery via `/v1/models`, and structured JSON output. When `StructuredOutputSchema` is provided, GnOuGo sends a synthetic `gnougo_structured_output` client tool with the schema as `input_schema` and forces it through `tool_choice`, then maps the returned `tool_use.input` to `LLMClientResponse.Json`. Because Anthropic forced tool use is incompatible with extended thinking, the provider omits `thinking` for these structured-output calls.

## Reasoning / Thinking effort

`LLMClientRequest.Reasoning` (and `LLMRequest.Reasoning` in `GnOuGo.Flow.Core`) controls the
"thinking" / reasoning effort of capable models without hard-coding any provider-specific field.

Accepted values: `"minimal" | "low" | "medium" | "high" | "max" | "auto"` (or `null`).

| Value           | OpenAI / Copilot (GitHub Models)        | Ollama                | Anthropic / Claude         |
|-----------------|-----------------------------------------|-----------------------|----------------------------|
| `null` / `auto` | field omitted (provider default)        | field omitted         | field omitted              |
| `minimal`       | `reasoning_effort: "minimal"`           | `think: true`         | `thinking.budget_tokens=1024` |
| `low`           | `reasoning_effort: "low"`               | `think: true`         | `thinking.budget_tokens=1024` |
| `medium`        | `reasoning_effort: "medium"`            | `think: true`         | `thinking.budget_tokens=4096` |
| `high` / `max`  | `reasoning_effort: "high"`              | `think: true`         | `thinking.budget_tokens=8192/16000` |
| `none` / `off`  | (treated as `auto`)                     | `think: false`        | field omitted              |

For Claude Opus 4.7 and later Opus models, Anthropic no longer accepts fixed `thinking.budget_tokens`. The provider keeps the same GnOuGo `Reasoning` values and sends `thinking.type=adaptive` with `output_config.effort` instead. For example, `Reasoning="high"` becomes:

```json
{
  "thinking": { "type": "adaptive" },
  "output_config": { "effort": "high" }
}
```

These models also reject non-default sampling parameters. GnOuGo marks `temperature`, `top_p`, and `top_k` as unsupported in model metadata and the Anthropic provider omits `temperature` defensively for Opus 4.7+ requests.

Models that don't support thinking have the field removed by `LLMRequestSanitizer` before the provider call.
Provider-specific mapping lives in `ChatRequestBuilder.NormalizeOpenAiReasoning`, `NormalizeOllamaThink`, `AnthropicLLMProvider.NormalizeThinkingBudget`, and `AnthropicLLMProvider.NormalizeAnthropicEffort`.

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
| `LLMModelMetadataResolver` | Merges metadata and resolves exact, alias, same-provider fuzzy, or heuristic matches |
| `LLMModelMetadataResolution` | Metadata plus match provenance, matched model/provider, and fuzzy similarity |
| `LLMRequestSanitizer` | Removes unsupported optional request parameters |
| `ChatRequestBuilder` | AOT-friendly JSON request builder |
| `ChatResponseParser` | Response parser for all providers |
| `CopilotEndpoints` / `OpenAiEndpoints` / `OllamaEndpoints` | URL helpers |
