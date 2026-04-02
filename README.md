
![GnouGo.png](GnouGo.png)

# 🐻 GnouGo : The Friendly Bear Agent

> [!WARNING]
> **WORK IN PROGRESS** — features, APIs, and project structure may still change.

GnouGos have existed forever — they are extremely kind, a little magical, and always appear at the right moment.
They possess an enormous amount of knowledge.
For them to help you, you will need to explain clearly what you want. They won't always understand on the first try,
so take the time to re-explain.

Give them clear and simple instructions and they will provide you with invaluable services.

Their main objective: your safety and saving you time!

> ⚠️ GnouGo can make mistakes. That's okay! Forgive him and rephrase your instructions more clearly. He learns better when you talk to him nicely. The more precise you are, the more effective he'll be.

---

## 🧱 Project Building Blocks

The GnOuGo project is composed of several complementary families:

| Family | Description |
|---|---|
| **GnOuGo.Agent** | End-user chat experience: local web app, desktop host, and shared contracts for the main assistant UI. |
| **GnOuGo.Flow** | Workflow automation platform: DSL execution engine, CLI/server hosting, browser automation, command execution, and workflow user data. |
| **GnOuGo.Diff** | Audit and comparison tools: revision storage, diff computation, API, UI, and sample-data utilities. |
| **GnOuGo.DocIngestor** | Document ingestion pipeline: extract, chunk, embed, and expose documents through CLI and server components. |
| **GnOuGo.KeyVault** | Secret management services: encrypted storage, tenant-aware vault features, and management APIs. |
| **GnOuGo.OtlpCollector** | OpenTelemetry ingestion stack: multi-tenant OTLP collection plus tooling to send and inspect telemetry data. |

Supporting libraries such as **GnOuGo.AI.Core**, **GnOuGo.Auth.Core**, and **GnOuGo.VectorDbDisk** provide shared AI, authentication, and storage foundations across these families.

---

## 🚀 Getting Started

### Prerequisites

- **.NET 10.0** (configured in `global.json`)
- **Node.js** (18+) for frontend tooling
- **npm** for JavaScript dependencies

### Quick Start

#### 1. Build the UI (Frontend Assets)

The frontend is built with **Vite** and generates `wwwroot/ui/app.js` and `wwwroot/ui/app.css`.

**Windows (PowerShell):**
```powershell
.\scripts\build-ui.ps1
```

**macOS/Linux (Bash):**
```bash
./scripts/build-ui.sh
```

**Manual build:**
```bash
cd src/GnOuGo.Agent.Server/ClientApp
npm install
npm run build
```

> **Note**: The UI assets are required before running any web-based component. If you skip this step, you'll encounter a JavaScript interop error: `Could not find 'GnOuGo.Agent.storage.load'`.

#### 2. Run the Agent Server (Web UI)

Starts an ASP.NET Core Blazor server with Minimal API endpoints at `http://localhost:5000`.

```bash
dotnet run --project src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj
```

Then open your browser to:
```
http://localhost:5000
```

#### 3. Run the Desktop Application

Launches the cross-platform desktop application using **Photino.NET** (WebView2 on Windows, native on macOS/Linux).

```bash
dotnet run --project src/GnOuGo.Agent.Desktop/GnOuGo.Agent.Desktop.csproj
```

---

## 🔄 Development Workflows

### Full-Stack Development

1. **Build UI once** (or in watch mode):
   ```bash
   cd src/GnOuGo.Agent.Server/ClientApp
   npm run dev  # Watch mode for live reloading
   ```

2. **Run the server** in another terminal:
   ```bash
   dotnet run --project src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj --configuration Debug
   ```

3. **Make changes** to:
   - C# backend: Auto-reloaded by `dotnet run`
   - TypeScript/SCSS: Auto-built by Vite dev server

### Building Components Individually

Each component can be built and tested independently:

#### GnOuGo.AI.Core
Shared AI/LLM provider infrastructure:
```bash
dotnet build src/GnOuGo.AI.Core/GnOuGo.AI.Core.csproj
dotnet test tests/GnOuGo.AI.Core.Tests/GnOuGo.AI.Core.Tests.csproj
```

#### GnOuGo.Flow
Workflow DSL execution engine:
```bash
dotnet build src/GnOuGo.Flow.Core/GnOuGo.Flow.Core.csproj
dotnet test tests/GnOuGo.Flow.Tests/GnOuGo.Flow.Tests.csproj
```

#### GnOuGo.DocIngestor
Document processing pipeline:
```bash
dotnet build src/GnOuGo.DocIngestor.Core/GnOuGo.DocIngestor.Core.csproj
dotnet test tests/GnOuGo.DocIngestor.Tests/GnOuGo.DocIngestor.Tests.csproj
```

---

## 🏗️ Build & Publish

### Debug Build

```bash
dotnet build GnOuGo.Agent.sln --configuration Debug
```

### Release Build

```bash
dotnet build GnOuGo.Agent.sln --configuration Release
```

### Publish Desktop App (Native AOT - Optimized)

Publishes a self-contained, trimmed, and AOT-compiled binary:

**Windows:**
```bash
dotnet publish src/GnOuGo.Agent.Desktop/GnOuGo.Agent.Desktop.csproj -c Release -o ./publish
```

**macOS (ARM64):**
```bash
./scripts/publish-desktop-osx-arm64-aot.sh
```

**Server (macOS ARM64):**
```bash
./scripts/publish-server-osx-arm64-aot.sh
```

### Publish Server (Container-Ready)

```bash
dotnet publish src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj -c Release -o ./publish
```

> See `src/GnOuGo.Agent.Server/Dockerfile` for containerization.

---

## 🧪 Testing

Run all tests:
```bash
dotnet test GnOuGo.Agent.sln
```

Run a specific test project:
```bash
dotnet test tests/GnOuGo.AI.Core.Tests/GnOuGo.AI.Core.Tests.csproj -v detailed
```

---

## 📝 Project Structure

```
GnouGo/
├── src/
│   ├── GnOuGo.Agent.Desktop/          # Desktop app (Photino.NET)
│   ├── GnOuGo.Agent.Server/           # Web server (Blazor + Minimal API)
│   │   └── ClientApp/                 # Frontend (TypeScript + Vite)
│   ├── GnOuGo.Agent.Shared/           # Shared DTOs
│   ├── GnOuGo.AI.Core/                # LLM providers & embeddings
│   ├── GnOuGo.Auth.Core/              # Authentication & authorization
│   ├── GnOuGo.Flow.{Core,Server,Cli}/ # Workflow engine
│   ├── GnOuGo.Diff.{Core,Server,Cli}/ # Diff & audit tools
│   ├── GnOuGo.DocIngestor.{Core,...}/ # Document processing
│   ├── GnOuGo.KeyVault.{Core,...}/    # Secret management
│   └── GnOuGo.OtlpCollector.{...}/    # OpenTelemetry stack
├── tests/                             # Unit & integration tests
├── scripts/                           # Build & deployment scripts
└── GnOuGo.Agent.sln                   # Solution file

```

---

## ⚙️ Configuration

### Server Settings

- `appsettings.json` - Production configuration
- `appsettings.Development.json` - Development overrides

### Environment Variables

Key configuration options can be set via environment variables (see individual component READMEs).

---

## 📚 Additional Resources

- [GnOuGo.Agent Server README](src/GnOuGo.Agent.Server/README.md) - Frontend build details
- [GnOuGo.AI.Core README](src/GnOuGo.AI.Core/README.md) - LLM provider setup
- [agents.md](agents.md) - Agent architecture & design patterns
- [LICENSE.md](LICENSE.md) - Project license

---

