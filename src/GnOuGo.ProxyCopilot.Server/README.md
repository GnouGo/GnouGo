# GnOuGo.ProxyCopilot.Server

A standalone .NET 10 loopback proxy for VS Code Chat and Agent mode. Configure AXA or other LLM endpoints in typed `appsettings.json`, supply credentials through environment overrides, and inspect live traffic in the React dashboard.

![Live traffic with synthetic providers](docs/screenshots/live-traffic.png)

The server references only `GnOuGo.AI.Core`, `GnOuGo.Auth.Core`, and `GnOuGo.Observability.Core`. It needs no database or running GnOuGo services. Configuration is loaded once at startup; restart after changes. Traffic stays in memory and is discarded on exit.

## Build and run

From the repository root, using the SDK pinned by `global.json` and Node.js 22.20+:

```bash
corepack pnpm install --frozen-lockfile
corepack pnpm --filter gnougo-proxy-copilot-client build
dotnet build src/GnOuGo.ProxyCopilot.Server/GnOuGo.ProxyCopilot.Server.csproj
dotnet run --project src/GnOuGo.ProxyCopilot.Server
```

Open **http://127.0.0.1:5087/ui/**. The default configuration has no providers and shows setup instructions. Vite builds into `wwwroot/ui`; the server copies these assets to its build output and publish directory.

For frontend development, run the server and `corepack pnpm --filter gnougo-proxy-copilot-client dev`. The Vite development proxy forwards `/api` to the loopback server with the matching origin; the production server itself does not allow cross-origin access.

## Configure providers

Merge the desired `ProxyCopilot.Providers` entries from these examples into the server's `appsettings.json`:

| Example | Authentication | Upstream API |
|---|---|---|
| [AXA client secret](examples/axa-client-secret.json) | OIDC discovery + `client_credentials` + `client_secret_basic` | Chat Completions |
| [AXA private key](examples/axa-private-key.json) | OIDC discovery + RS256 `private_key_jwt` | Chat Completions |
| [OpenAI-compatible](examples/openai.json) | Bearer API key | Chat Completions |
| [Copilot](examples/copilot.json) | `GITHUB_TOKEN`, then `COPILOT_API_KEY` | Configured Copilot/GitHub Models Chat Completions endpoint |
| [Anthropic](examples/anthropic.json) | `x-api-key` | Native Messages |
| [Ollama](examples/ollama.json) | None | Native `/api/chat` |

Replace the example URL, upstream model ID, issuer, client ID, scopes, and metadata with your actual values. Example prices are zero placeholders, not provider pricing. Multiple named providers of the same type are supported. Names and model aliases use letters, digits, `.`, `_`, or `-`; the exposed ID is the exact, case-sensitive `<provider>/<alias>`, such as `axa/code`. The actual upstream model ID is forwarded unchanged, including vendor prefixes.

Connection options reuse AI.Core's `ModelProviderOptions`; metadata reuses `LLMModelMetadata`. Set `Connection.Type` explicitly to `openai`, `copilot`, `anthropic`, or `ollama`. Model input/output limits are required. Set `Capabilities.SupportsTools` to `true` only for models supporting tool calls. Advertised capabilities are restricted to this proxy's text/tool support even when richer metadata is configured.

`Authentication` accepts `None`, `ApiKey`, `OidcClientSecret`, `OidcPrivateKey`, or `CopilotEnvironment`. OIDC modes also work with native providers behind enterprise gateways. Anthropic API keys use `x-api-key`; OIDC access tokens use bearer authentication. Conflicting credentials and malformed keys fail startup without printing values.

Supply secrets to the process, for example through your shell's secure credential tooling:

```text
ProxyCopilot__Providers__axa__Connection__ClientSecret
ProxyCopilot__Providers__axa__Connection__PrivateKeyPem
ProxyCopilot__Providers__openai__Connection__ApiKey
ProxyCopilot__Providers__anthropic__Connection__ApiKey
```

Choose exactly one OIDC credential. `PrivateKeyPem` contains the PEM text, not a filename or a literal `\n`-escaped string. Do not commit credentials or persist them in plaintext configuration files. The server never writes configuration. OIDC tokens are cached per provider, refreshed under a lock before actual expiry, and never sent to VS Code or included in traffic headers.

The OIDC baseline uses `<Issuer>/.well-known/openid-configuration`, the discovered token endpoint as JWT audience, RSA/RS256 assertions without a `kid`, and client-secret HTTP Basic authentication. Custom audiences, `kid`, explicit token endpoints, `client_secret_post`, and custom gateway authentication headers are not implemented.

`Connection.ApiVersion` appends `api-version` for OpenAI-compatible endpoints. Inference uses Chat Completions; set `RequestPolicy.BackgroundProtocol` to `ChatCompletions`. Output limits are clamped to configured model/provider ceilings. Anthropic requires `UnspecifiedOutputTokens: Configured` and a positive `DefaultMaxOutputTokens`. Native adapters map temperature, top-p, stop sequences, and output limits. Unsupported options fail before dispatch.

Retries default to one attempt. Set `RetryPolicy.MaxAttempts` to enable bounded retries of HTTP **429** or **503** rejections. `Retry-After` is honored within `MaxTotalDelayMilliseconds`; exceeding that budget returns the rejection. `MaxUncertainRetries` must remain zero. Connection failures and started streams are never replayed. `AttemptTimeoutMilliseconds` covers authentication, sending, and reading an attempt; client cancellation cancels the upstream request.

## Connect VS Code

1. Run **Chat: Manage Language Models**, then **Add Models → Custom Endpoint**.
2. Choose **Chat Completions**. The local proxy requires no client API key; leave it empty or enter a placeholder if your editor prompts for one. Incoming credentials are never forwarded.
3. Copy the credential-free configuration from the dashboard's **VS Code setup** tab into `chatLanguageModels.json`.
4. Select the configured model in Chat. For Agent mode, the model must support tools.

The generated entries include full URLs, such as `http://127.0.0.1:5087/v1/chat/completions`, and explicit model limits. An organization may disable custom model access through its Copilot policies. See [VS Code custom endpoint documentation](https://code.visualstudio.com/docs/copilot/customization/language-models#_add-a-custom-endpoint-model).

V1 supports text conversations, system/developer messages, function tools, parallel tool calls/results, streaming, non-streaming, finish reasons, and reported usage. VS Code executes tools. Ollama tool results are restored to their original call order before forwarding; Anthropic preserves explicit tool-use IDs. Anthropic system/developer messages must precede the conversation.

Inline code suggestions, images, embeddings, native structured-output/reasoning translation, Responses/background execution, hosted tool execution, and embedded local runtimes are outside v1. OpenAI-compatible requests retain additional fields unless explicitly rejected by configured metadata; native adapters reject fields they cannot translate. A malformed or interrupted upstream stream is marked failed and closed, without inventing a successful completion.

## HTTP and traffic interfaces

| Route | Purpose |
|---|---|
| `GET /health` | Process liveness; does not call providers |
| `GET /v1/models` | Explicitly configured model aliases |
| `POST /v1/chat/completions` | Streaming or non-streaming inference |
| `GET /api/setup` | Credential-free VS Code configuration |
| `GET /api/traffic` | Versioned summaries, newest first |
| `GET /api/traffic/{id}` | Summary and redacted client/upstream request/response bodies |
| `GET /api/traffic/events` | SSE `changed` invalidations; fetch snapshots after each notification |
| `DELETE /api/traffic` | Clear in-memory captures |

The dashboard shows provider/model/status filters, duration, time to first token, usage, conversation messages, tools, and raw payloads on both sides of translations. Raw bodies are captured only for calls resolving to configured models; admission errors such as invalid JSON or unknown models return structured errors without a traffic record. Authentication exchange bodies and HTTP credentials are never captured. Known credentials and credential-shaped JSON fields are redacted before captures are exposed, including credentials split across reads.

Defaults: 200 calls, 256 KiB per captured body, 64 MiB of captured body buffers/credential storage, 32 concurrent inference requests, 2 MiB request bodies, 1 MiB streaming frames, 8 MiB non-streaming responses, and 16 dashboard subscribers. Configure the first three through `ProxyCopilot.Capture`; concurrency and request size use `MaxConcurrentRequests` and `MaxRequestBytes`. The total budget evicts oldest calls, including in-flight captures when necessary. Capture truncation/eviction never truncates inference. Pausing the UI pauses viewing; capture continues. Browser notifications are coalesced and cannot block inference.

Only loopback listen addresses and same-origin browser requests are accepted. Configure the port with `Urls`; Kestrel endpoint overrides are rejected. Host validation also prevents DNS rebinding to the dashboard. Upstream TLS verification remains enabled; install your organization's CA using the operating system trust configuration. The app does not disable certificate validation or follow credential-bearing redirects.

`ProxyCopilot.TenantId` defaults to `local`. Enable optional OpenTelemetry through the shared `OpenTelemetry` section. The proxy emits request status, duration, model/provider, and tenant metadata, without prompt/response bodies or authentication HTTP traces. Nothing is persisted by the application.

## Test and publish

```bash
dotnet test tests/GnOuGo.ProxyCopilot.Server.Tests/GnOuGo.ProxyCopilot.Server.Tests.csproj
dotnet test tests/GnOuGo.Auth.Core.Tests/GnOuGo.Auth.Core.Tests.csproj
corepack pnpm --filter gnougo-proxy-copilot-client test

dotnet publish src/GnOuGo.ProxyCopilot.Server/GnOuGo.ProxyCopilot.Server.csproj \
  -c Release -r osx-arm64 --self-contained true -p:PublishAot=true \
  -o artifacts/publish/proxy-copilot-osx-arm64

python3 scripts/smoke-proxy-copilot.py \
  --binary artifacts/publish/proxy-copilot-osx-arm64/GnOuGo.ProxyCopilot.Server
```

Use `linux-x64` on Linux or the matching host RID. Native AOT requires the platform's native compiler. Publish builds the frontend automatically; `-p:SkipClientBuild=true` uses already-built assets. A framework-dependent publish is also supported. Deploy the complete publish directory, including `appsettings.json` and `wwwroot/ui/`, then set credentials in the launch environment. The executable resolves its own configuration/static assets independently of the caller's working directory.

For browser validation, add `--serve --port 5087` to the smoke command in one terminal. In another:

```bash
npm install --prefix /tmp/gnougo-proxy-browser playwright@1.63.0
/tmp/gnougo-proxy-browser/node_modules/.bin/playwright install chromium
PLAYWRIGHT_MODULE_PATH=/tmp/gnougo-proxy-browser/node_modules/playwright/index.mjs \
  node scripts/smoke-proxy-copilot-ui.mjs
```

The smoke environment uses synthetic credentials and local fake providers. Browser checks cover live output before completion, raw native payloads, filters, setup, pause/resume, reconnect, clearing, and mobile layout. Screenshots go to `artifacts/proxy-copilot/screenshots`. `.github/workflows/test-proxy-copilot.yml` runs the isolated tests and published-binary/browser checks on Linux.

## AXA acceptance

- Replace the example endpoint, issuer, client ID, scopes, model ID, and metadata with AXA's actual values; supply either secret or PEM through the environment.
- Confirm your issuer accepts the documented OIDC baseline and the machine trusts the internal CA.
- Select the configured alias in VS Code; verify incremental text, a complete tool-call/result cycle, cancellation, and token refresh across expiry.
- Confirm that the dashboard shows inputs/outputs while credentials remain redacted.

Automated tests prove behavior against protocol fixtures. Actual AXA connectivity and VS Code policy availability require validation in the user's environment.
