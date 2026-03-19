# GnOuGo.AI.Core

Low-level, AOT-friendly library for calling LLM providers.  
Provides a **provider-agnostic routing layer** so that the rest of the system never deals with HTTP specifics.

## Supported Providers

| Provider key | Type      | Description                                      |
|-------------|-----------|--------------------------------------------------|
| `OpenAi`    | `openai`  | OpenAI, Azure OpenAI, or any compatible endpoint |
| `Ollama`    | `ollama`  | Local Ollama server                              |
| `Copilot`   | `copilot` | GitHub Copilot / GitHub Models API               |

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
    }
  }
}
```

### Copilot / GitHub Models

The Copilot provider connects to the [GitHub Models](https://github.com/marketplace/models) inference endpoint, which is OpenAI-compatible.

**Authentication** (in priority order):
1. `ApiKey` in the configuration
2. `GITHUB_TOKEN` environment variable
3. `COPILOT_API_KEY` environment variable

**Model names** can use the vendor prefix format (`openai/gpt-4.1`, `anthropic/claude-sonnet-4`) — the prefix is automatically stripped before sending to the API. Plain names like `gpt-4.1` or `o4-mini` also work.

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
| `ChatRequestBuilder` | AOT-friendly JSON request builder |
| `ChatResponseParser` | Response parser for all providers |
| `CopilotEndpoints` / `OpenAiEndpoints` / `OllamaEndpoints` | URL helpers |

