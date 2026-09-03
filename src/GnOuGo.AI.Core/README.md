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
| `Local`     | `local`   | Embedded runtime supplied by `GnOuGo.AI.Local`   |

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

`AI.Core` owns only the stable local-runtime boundary: `ILocalLLMRuntime`,
`ILocalModelManager`, model lifecycle/progress DTOs, typed local failures, and
`LocalLLMProvider`. Native LLamaSharp implementation details remain isolated in the
separately publishable `GnOuGo.AI.Local` package.

`LLMStructuredOutputValidator` is the shared AOT-friendly JSON-schema instance
validator used by both `GnOuGo.Flow.Core` and local inference. When the selected
provider is `local`, routing performs at most two local attempts. A structured
second attempt receives sanitized validation feedback. A configured non-local
`LLM.Fallback` is then called at most once for load, inference, or structured-output
failure. Caller cancellation is never retried or sent to a fallback, and a fallback
cannot point back to `local`.

## HTTP resilience

OpenAI, Ollama, Copilot/GitHub Models, and Anthropic HTTP operations make at most four
attempts for `425`, `429`, `500`, `502`, `503`, and `504`. Every attempt receives a fresh
request. A valid `Retry-After` delta or HTTP date is honored; otherwise recovery uses
full-jitter exponential backoff from one second, capped at 30 seconds per wait and 60 seconds
cumulatively. A delay outside that budget stops recovery instead of holding a workflow open.

Before replay, GnOuGo inspects a bounded error envelope. Quota/billing, authentication,
and authorization failures are terminal even when a gateway transports them as `429`.
Other `4xx`, transport failures with uncertain delivery, timeouts, caller cancellation, and
unknown statuses are never replayed. Logs contain only the operation, status, attempt, selected
delay, and exhaustion state—not endpoints, prompts, payloads, response bodies, tokens, headers,
or credentials.

`RoutingLLMClient` exposes provider failures through the redacted
`LLMProviderException` contract. `LLMProviderFailureKind` distinguishes transport,
timeout, ordinary rate limiting, service unavailability, authentication,
authorization, quota or billing exhaustion, invalid requests, unavailable models,
and unknown terminal failures. Transport, timeout, rate-limit, and service failures
are retryable; the remaining categories fail immediately. Exception metadata may contain an
HTTP status, actual attempt count, retry-exhaustion flag, accepted `Retry-After`, and a safe
provider error code, but never a response body, endpoint, credential, request prompt, or raw
user content.

### OpenAI background Responses

`LLMClientRequest.UseBackgroundMode=true` routes OpenAI calls through the Responses API with
`background: true`. Structured-output requests keep their normalized JSON Schema, name, and
strictness under `text.format`; reasoning effort and `max_output_tokens` are preserved as well.
GnOuGo polls only responses whose status is `queued` or `in_progress`, returns `completed`
responses, and surfaces terminal or unexpected statuses without silently switching protocols.

`RequestPolicy.BackgroundProtocol` controls background calls. `Auto` probes Responses and uses
Chat Completions only when the HTTP contract proves the route unsupported. `Responses` requires
that protocol, while `ChatCompletions` bypasses the probe. In `Auto`, route-level `404`, `405`,
and `501` results are cached immediately, even if the first Chat fallback later fails transiently.
Request-specific errors are never cached. Ambiguous `400`/`422` incompatibility is cached only
after a successful Chat fallback. The official OpenAI endpoint does not use a compatibility
downgrade.

The Chat request first preserves strict JSON Schema output, reasoning effort, tools, and the
output-token limit through `max_completion_tokens`. A non-official endpoint that returns `400`
or `422` for that request gets one legacy-compatible retry that omits only
`max_completion_tokens`, provided the error is generic or identifies that token parameter rather
than another explicit parameter. Official OpenAI endpoints never use this legacy retry. Successful
compatibility results are cached for 65 minutes: Responses support is keyed by endpoint, while the
legacy Chat requirement is keyed by endpoint, API version, and model. Provider exceptions and
compatibility logs preserve only sanitized status and policy details.

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
    "Fallback": {
      "Provider": "Copilot",
      "Model": "gpt-4.1"
    },
    "Models": {
      "OpenAi": {
        "Url": "https://api.openai.com/v1",
        "ApiKey": "sk-..."             // or set OPENAI_API_KEY env var
      },
      "CompatibleGateway": {
        "Url": "https://gateway.example/v1",
        "Type": "openai",
        "ApiVersion": "YYYY-MM-DD-preview",
        "RequestPolicy": {
          "BackgroundProtocol": "ChatCompletions",
          "UnspecifiedOutputTokens": "Configured",
          "DefaultMaxOutputTokens": 4096,
          "MaxOutputTokensCap": 8192
        },
        "RetryPolicy": {
          "MaxAttempts": 4,
          "BaseDelayMilliseconds": 1000,
          "MaxDelayMilliseconds": 30000,
          "MaxTotalDelayMilliseconds": 60000,
          "HonorRetryAfter": true
        }
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

`MaxOutputTokens` in model metadata is always a supported ceiling, not an implicit request
default. With the standards-based `UnspecifiedOutputTokens: Omit` default, callers that leave
their limit unset emit neither `max_completion_tokens` nor `max_output_tokens`. An explicit caller
limit is clamped to the model ceiling and optional provider cap. `Configured` supplies the stated
default; `ModelMaximum` retains the former ceiling-as-default behavior only as an explicit legacy
choice. Invalid combinations, including `Configured` without a positive default or a default
above the provider cap, fail configuration validation.

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
| `ILocalLLMRuntime` / `ILocalModelManager` | Stable embedded-runtime and model-lifecycle contracts |
| `RoutingLLMClient` | Routes `LLMClientRequest` to the correct provider |
| `LLMStructuredOutputValidator` | Shared AOT-friendly JSON-schema instance validation |
| `LLMOptions` / `ModelProviderOptions` | Configuration model |
| `LLMModelMetadataResolver` | Merges metadata and resolves exact, alias, same-provider fuzzy, or heuristic matches |
| `LLMModelMetadataResolution` | Metadata plus match provenance, matched model/provider, and fuzzy similarity |
| `LLMRequestSanitizer` | Removes unsupported optional request parameters |
| `ChatRequestBuilder` | AOT-friendly JSON request builder |
| `ChatResponseParser` | Response parser for all providers |
| `CopilotEndpoints` / `OpenAiEndpoints` / `OllamaEndpoints` | URL helpers |
