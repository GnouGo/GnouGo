# AGENTS.md — GnOuGo (cross-cutting rules)

Use English only.

## Architecture Principles
- Split into **independent** **libraries / APIs / CLIs**.
- Each component must be:
  - **publishable** separately (package),
  - **testable** separately (dedicated unit tests),
  - **deployable** without implicit dependencies.
- **Minimal coupling**:
  - dependencies only through **interfaces** (abstractions) and stable contracts,
  - no circular dependencies.
- **Flow.Core autonomy**:
  - `GnOuGo.Flow.Core` must never reference another `GnOuGo.*` project or package,
  - Flow.Core owns provider-neutral interfaces and contracts; concrete AI, MCP, and host integrations belong to consumers or `GnOuGo.Flow.Integrations` and must be injected through those interfaces.
- **Provider-neutral planning behavior**:
  - Flow.Core planning, clarification, inference, and validation behavior must derive only from stable Flow contracts, explicit configuration, runtime inputs, declared step or MCP schemas, and provider-supplied provider-neutral metadata,
  - runtime behavior must never branch on provider, server, or tool names, catalog identifiers, URLs, domain nouns, use-case phrases, or prompt keywords,
  - provider- and use-case-specific semantics belong in producer metadata or injected integrations outside Flow.Core,
  - every AI-produced classification or contract must be validated deterministically; insufficient evidence must trigger generic human clarification when eligible or fail closed, never a provider-specific heuristic.

---

## Technology Stack

- **Runtime**: .NET 10 (`global.json` pins SDK `10.0.300`, `rollForward: latestFeature`)
- **Persistence**: Entity Framework Core + SQLite is mandatory for HTTP MCP and Server components that already own EF-backed persistence; plain `Microsoft.Data.Sqlite` is reserved for components explicitly designed around an owner-scoped raw persistence implementation
- **Frontend**: Vite + pnpm (workspace managed via `pnpm-workspace.yaml`); built into `wwwroot/ui/`
- **Python libraries**: located under `librairies/python/` (note the non-standard spelling) and `clients/python/`
- **MCP SDK**: `ModelContextProtocol` NuGet package; shared helpers in `GnOuGo.Mcp.Core`
- **Observability**: `GnOuGo.Observability.Core` (`builder.AddGnOuGoOpenTelemetry("ServiceName")`)

---

## Actual Mono-repo Structure

```
src/GnOuGo.<LibName>/              # core library (publishable NuGet package)
src/GnOuGo.<LibName>.Server/       # ASP.NET Core API or hosted service
  ClientApp/                       # React/Vite front-end (pnpm)
  wwwroot/                         # Vite build output (committed for development)
src/GnOuGo.<LibName>.Cli/          # CLI (console, may be Native AOT)
src/GnOuGo.<LibName>.Mcp/          # MCP server — stdio OR HTTP
src/GnOuGo.Agent.Server/           # main Blazor UI + Minimal API + in-process MCP host
src/GnOuGo.Agent.Desktop/          # Photino.NET desktop shell (wraps Agent.Server)
src/GnOuGo.Agent.Shared/           # shared DTOs between Agent.Server and consumers
tests/GnOuGo.<LibName>.Tests/      # unit tests
librairies/python/                 # Python libraries (note spelling)
clients/python/                    # Python client packages
scripts/                           # build, publish, and metadata-update scripts
```

> **Note**: the naming convention uses `.Server` (not `.Api`) for hosted ASP.NET Core components.
> The final Blazor application is `GnOuGo.Agent.Server` + `GnOuGo.Agent.Desktop`, **not** a single `GnOuGo.Agent/` folder.

---

## Component Map

| Family | Component(s) | Notes |
|---|---|---|
| **GnOuGo.Agent** | `Agent.Server`, `Agent.Desktop`, `Agent.Shared`, `Agent.Mcp` | Blazor UI + Photino desktop shell. Agent definitions stored as `{name}.yaml` in workspace. |
| **GnOuGo.AI** | `AI.Core` | AOT-friendly LLM routing: OpenAI, Ollama, Copilot, Anthropic. Published NuGet package. |
| **GnOuGo.Flow** | `Flow.Core`, `Flow.Integrations`, `Flow.Cli`, `Flow.Server`, `Flow.Mermaid` | Autonomous YAML workflow DSL core plus separately publishable AI/MCP integrations, NativeAOT-compatible CLI/server, and Mermaid rendering. |
| **GnOuGo.Diff** | `Diff.Core`, `Diff.Cli`, `Diff.Server` | Revision storage, diff computation, API + UI. |
| **GnOuGo.Files** | `Files.Server` | Temporary streamed file upload/download API with SQLite metadata + configurable TTL purge worker. |
| **GnOuGo.DocIngestor** | `DocIngestor.Core`, `DocIngestor.Cli`, `DocIngestor.Mcp`, `DocIngestor.Server` | Document ingestion pipeline (extract, chunk, embed, vector search). |
| **GnOuGo.KeyVault** | `KeyVault.Core`, `KeyVault.Mcp`, `KeyVault.Server` | Encrypted secret storage, tenant-aware. Secrets decrypted in-memory only. |
| **GnOuGo.OtlpCollector** | `OtlpCollector.Server`, `OtlpCollector.Cli` | Multi-tenant OTLP ingest stack. Embedded in `Agent.Server` on ports 4317/4318. |
| **MCP servers** | `Agent.Mcp` (HTTP), `KeyVault.Mcp` (HTTP), `Browser.Mcp` (stdio), `Cmd.Mcp` (stdio), `Git.Mcp` (stdio), `Document.Mcp` (stdio), `GithubCopilot.Mcp` (stdio), `DocIngestor.Mcp` (HTTP) | |
| **Shared libs** | `Auth.Core`, `Mcp.Core`, `Observability.Core`, `Workspace` | Cross-cutting helpers. |

---

## Cross-cutting Technical Rules
- **Multi-tenant**: all persisted entities must carry a `TenantId`.
- **Observability**: **OpenTelemetry** instrumentation (traces/metrics/logs) with `TenantId` propagation.
- **Security**:
  - secrets stored **encrypted** in the database via `GnOuGo.KeyVault.Core`,
  - no sensitive data in plain text at rest,
  - MCP servers and `GnOuGo.Agent.Server` must never read or write configuration directly through SQL; all configuration access must go through the public abstractions provided by the central `GnOuGo.KeyVault.Core` library. This shared boundary ensures that configuration values managed from `GnOuGo.Agent.Server` can be securely and consistently consumed by MCP servers.
- **Generic KeyVault boundary**:
  - every `GnOuGo.KeyVault.*` project may contain only KeyVault-domain types and generic storage, encryption, audit, tenant, secret, and record infrastructure,
  - KeyVault projects must never reference consumer namespaces, models, constants, secret-key prefixes, configuration paths, or JSON schemas,
  - consumers own their secret names, serialization formats, typed settings, mapping, validation, precedence, and failure policy,
  - MCP servers must read configuration only through public `GnOuGo.KeyVault.Core` abstractions, map it into their own configuration namespace before typed options are resolved, and never query the KeyVault database through SQL,
  - KeyVault configuration is a highest-priority consumer configuration overlay. Unavailable optional KeyVault storage falls back to the consumer's base settings with a redacted warning; a present malformed, ambiguous, or invalid value must fail startup without logging the value,
  - `GnOuGo.Agent.Server` may manage encrypted values and propagate the KeyVault location. An MCP marked as a direct KeyVault reader receives only that location and must retrieve and interpret its own values through the generic KeyVault API; decrypted configuration must not be injected into its process environment.
- **Unit tests**:
  - one test project per library,
  - no mandatory dependency on other libraries for testing.
- **Workspace convention**: default working directory is `Desktop/GnOuGo`; databases under `.GnOuGo/data/`. Always use `GnOuGoWorkspace` helpers (`src/GnOuGo.Workspace`) for path resolution — never hard-code paths.
- **EF Core persistence is an important architectural requirement**: HTTP MCP and Server components that currently own EF Core persistence must continue using EF Core and SQLite. Do not replace EF Core with raw `Microsoft.Data.Sqlite` merely to remove compiler, trimming, single-file, or Native AOT warnings. AOT-exposed EF Core components must use compiled models and precompiled queries where applicable, narrowly documented suppressions for verified framework limitations, and published-binary persistence smoke tests.
- **AOT constraints**: raw `Microsoft.Data.Sqlite` is allowed only inside components already explicitly designed to own raw persistence (for example, the KeyVault Native AOT record-store implementation); it is not an authorized substitute for an existing EF Core implementation. Configuration consumers must still use the owning component's public abstractions. Prefer source-generated `System.Text.Json` contexts.
- **Warning-free builds**: do not leave warnings in solution builds, invoked frontend builds, trimmed publishes, or Native AOT publishes. Fix source or contract root causes and do not blanket-disable analyzers. Any unavoidable third-party or framework trim/AOT exception must be publish-local, documented with the exact dependency and reason, and covered by a published-binary smoke test.

---

## Expected Deliverables per Component
Depending on the need, a component may provide:
- a **library** (`.dll` + package),
- an **API** (ASP.NET Core),
- a **CLI** (console),
- a **front-end** in `ClientApp/` integrated with the API,
- dedicated **unit tests**.

---

## Build, Test & Publish Commands

### Build a single component
```bash
dotnet build src/GnOuGo.<Name>/GnOuGo.<Name>.csproj
```

### Run all tests
```bash
dotnet test GnOuGo.Agent.sln
```

### Run tests for one component
```bash
dotnet test tests/GnOuGo.<Name>.Tests/GnOuGo.<Name>.Tests.csproj
```

### Build the Agent.Server frontend (required before first run)
```bash
cd src/GnOuGo.Agent.Server/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm build
```

### Run Agent.Server locally
```bash
dotnet run --project src/GnOuGo.Agent.Server
# UI: http://localhost:5000  |  OTLP gRPC: 4317  |  OTLP HTTP: 4318
```

### Publish Agent.Server (trimmed, server/Docker)
```bash
dotnet publish src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishTrimmed=true -p:PublishSingleFile=true -p:PublishAot=false
```

### Publish a stdio MCP tool as Native AOT (example: GithubCopilot.Mcp)
```bash
dotnet publish src/GnOuGo.GithubCopilot.Mcp/GnOuGo.GithubCopilot.Mcp.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -p:PublishAot=true -p:PublishTrimmed=true -p:InvariantGlobalization=false \
  -p:SkipModelMetadataGeneration=true
```

### Update model metadata catalog
```bash
# macOS/Linux
bash scripts/update-model-metadata.sh --download-latest
# Windows
pwsh scripts/update-model-metadata.ps1 -DownloadLatest
```

---

## MCP Server Development Patterns

### Registering the GnOuGo tool-error normalizer (required in every MCP server)
```csharp
services.AddMcpServer(options =>
{
    options.ServerInfo = new Implementation { Name = "GnOuGo.Example.Mcp", Version = "1.0.0" };
    options.AddGnOuGoToolErrorNormalizer();   // from GnOuGo.Mcp.Core
});
```

### Structured error envelopes (returned as `IsError = true`)
Return `{ "success": false, "error_message": "..." }` or `{ "ok": false }` or `{ "code": "...", "message": "..." }` to have the normalizer mark the result as an MCP error automatically.

### stdio MCP — progress events side channel
For long-running stdio tools, write structured JSONL to **stderr** during execution; also include the same events in `progressEvents` in the final result.
`GnOuGo.Flow.Core` forwards stderr lines matching the expected shape as `gnougo-flow.step.thinking` telemetry events in real time.
```json
{ "kind": "session_create", "level": "thinking", "message": "Creating session.", "timestamp": "2026-06-15T00:00:00Z" }
```
Only `message` is required. Do **not** forward raw model chain-of-thought.

### HTTP MCP — mounted inside Agent.Server
HTTP MCP servers are mounted via proxy routes at `/mcp/<name>`. Port `0` in `appsettings.json` is replaced at startup with the actual bound port.

---

## Final Goal
Assemble components to produce:
- a **local agent** (MVP) based on SQLite,
- a **GnOuGo.Agent** application in **Blazor** targeting **desktop** (Photino.NET),
- with a build pipeline capable of producing a **Native AOT** and/or **maximum Trim** binary,
- without tight coupling between libraries.
- Keep each component's README.md up to date with build, test, and usage instructions.
