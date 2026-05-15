
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


## 📦 Install gnougo

Windows:

```powershell
winget install GnOuGo.Agent
```

macOS:

```bash
brew update
brew tap gnougo/tap
brew install --cask gnougo
# To run GnOuGo
sudo xattr -r -d com.apple.quarantine /Applications/GnOuGo.app
open /Applications/GnOuGo.app
```

Generic Linux:

Download the `gnougo-linux-*.tar.gz` archive from the GitHub Release first, then run:

```bash
tar -xzf gnougo-linux-x64.tar.gz
sudo install gnougo /usr/local/bin/gnougo
```

Ubuntu/Debian:

Download the matching `.deb` package from the GitHub Release first, then run:

```bash
sudo apt install ./gnougo_*_amd64.deb
```

You can also download the Windows, macOS, and Linux archives from the GitHub Releases page.

## Downloads

| Distribution | Package | Version / Availability | Downloads |
|---|---|---:|---:|
| GitHub Release | `v0.6.6` | <a href="https://github.com/GnouGo/GnouGo/releases/tag/v0.6.6"><img src="https://img.shields.io/github/v/release/GnouGo/GnouGo?label=release" alt="GitHub release version"></a> | <a href="https://github.com/GnouGo/GnouGo/releases/tag/v0.6.6"><img src="https://img.shields.io/github/downloads/GnouGo/GnouGo/v0.6.6/total?label=total" alt="Total downloads for v0.6.6"></a> |
| Windows | `GnOuGo.Agent` via Winget | <a href="https://github.com/GnouGo/GnouGo/tree/main/packaging/winget/GnOuGo.Agent"><img src="https://img.shields.io/winget/v/GnOuGo.Agent?label=winget" alt="Winget version"></a> | <a href="https://github.com/GnouGo/GnouGo/releases/tag/v0.6.6"><img src="https://img.shields.io/github/downloads/GnouGo/GnouGo/v0.6.6/gnougo-win-x64.zip?label=x64" alt="Windows x64 downloads"></a><br><a href="https://github.com/GnouGo/GnouGo/releases/tag/v0.6.6"><img src="https://img.shields.io/github/downloads/GnouGo/GnouGo/v0.6.6/gnougo-win-arm64.zip?label=arm64" alt="Windows arm64 downloads"></a> |
| macOS | `gnougo` via Homebrew Cask | <a href="https://github.com/GnouGo/GnouGo/tree/main/packaging/homebrew-tap/Casks"><img src="https://img.shields.io/badge/homebrew%20cask-gnougo-blue" alt="Homebrew cask availability"></a> | <a href="https://github.com/GnouGo/GnouGo/releases/tag/v0.6.6"><img src="https://img.shields.io/github/downloads/GnouGo/GnouGo/v0.6.6/gnougo-osx-x64.tar.gz?label=x64" alt="macOS x64 downloads"></a><br><a href="https://github.com/GnouGo/GnouGo/releases/tag/v0.6.6"><img src="https://img.shields.io/github/downloads/GnouGo/GnouGo/v0.6.6/gnougo-osx-arm64.tar.gz?label=arm64" alt="macOS arm64 downloads"></a> |

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

## 🔌 MCP Servers

| Server | Transport | Description |
|---|---|---|
| **GnOuGo.Agent.Mcp** | HTTP | Agent definitions, chat history, and user configuration (default LLM, default agent). |
| **GnOuGo.KeyVault.Mcp** | HTTP | Encrypted secret storage: tenant management, create/read/update/delete secrets. |
| **GnOuGo.Browser.Mcp** | stdio | Web navigation via Playwright: open pages, click, fill forms, screenshot, and more. |
| **GnOuGo.Cmd.Mcp** | stdio | Safe shell command execution through a strict allowlist (deny-by-default, cross-platform). |
| **GnOuGo.GithubCopilot.Mcp** | stdio | Local code operations: read files, search text, summarize a project, and suggest changes via GitHub Copilot. |
| **GnOuGo.Document.Mcp** | stdio | Read and write office/text documents (PDF, DOCX, XLSX, PPTX, Markdown, CSV, JSON…). |
| **GnOuGo.DocIngestor.Mcp** | HTTP | Document ingestion pipeline: download, chunk, embed into a vector store, and semantic search. |

---

## 📚 Published Libraries

The current GnOuGo release publishes these reusable libraries and packages:

| Library | Package | Version | Runtime | Downloads | Usage |
|---|---|---:|---:|---:|---|
| **GnOuGo.Auth.Core** (.NET) | <a href="https://www.nuget.org/packages/GnOuGo.Auth.Core">NuGet</a> | <a href="https://www.nuget.org/packages/GnOuGo.Auth.Core"><img src="https://img.shields.io/nuget/v/GnOuGo.Auth.Core.svg" alt="GnOuGo.Auth.Core version"></a> | <a href="https://www.nuget.org/packages/GnOuGo.Auth.Core"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt="GnOuGo.Auth.Core .NET 10.0"></a> | <a href="https://nugettrends.com/packages?ids=GnOuGo.Auth.Core"><img src="https://img.shields.io/nuget/dt/GnOuGo.Auth.Core.svg" alt="GnOuGo.Auth.Core downloads"></a> | Authentication abstractions and provider credential helpers for GnOuGo components. |
| **GnOuGo.AI.Core** (.NET) | <a href="https://www.nuget.org/packages/GnOuGo.AI.Core">NuGet</a> | <a href="https://www.nuget.org/packages/GnOuGo.AI.Core"><img src="https://img.shields.io/nuget/v/GnOuGo.AI.Core.svg" alt="GnOuGo.AI.Core version"></a> | <a href="https://www.nuget.org/packages/GnOuGo.AI.Core"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt="GnOuGo.AI.Core .NET 10.0"></a> | <a href="https://nugettrends.com/packages?ids=GnOuGo.AI.Core"><img src="https://img.shields.io/nuget/dt/GnOuGo.AI.Core.svg" alt="GnOuGo.AI.Core downloads"></a> | Low-level, AOT-friendly provider routing for LLM integrations. See [`src/GnOuGo.AI.Core/README.md`](src/GnOuGo.AI.Core/README.md). |
| **GnOuGo.Flow.Core** (.NET) | <a href="https://www.nuget.org/packages/GnOuGo.Flow.Core">NuGet</a> | <a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/v/GnOuGo.Flow.Core.svg" alt="GnOuGo.Flow.Core version"></a> | <a href="https://www.nuget.org/packages/GnOuGo.Flow.Core"><img src="https://img.shields.io/badge/.NET-10.0-blue.svg" alt="GnOuGo.Flow.Core .NET 10.0"></a> | <a href="https://nugettrends.com/packages?ids=GnOuGo.Flow.Core"><img src="https://img.shields.io/nuget/dt/GnOuGo.Flow.Core.svg" alt="GnOuGo.Flow.Core downloads"></a> | NativeAOT-compatible .NET workflow DSL engine. See [`src/GnOuGo.Flow.Core/README.md`](src/GnOuGo.Flow.Core/README.md). |
| **gnougo-flow-core** (Python) | <a href="https://pypi.org/project/gnougo-flow-core/">PyPI</a> | <a href="https://pypi.org/project/gnougo-flow-core/"><img src="https://img.shields.io/pypi/v/gnougo-flow-core.svg" alt="gnougo-flow-core version"></a> | <a href="https://pypi.org/project/gnougo-flow-core/"><img src="https://img.shields.io/pypi/pyversions/gnougo-flow-core.svg" alt="gnougo-flow-core Python versions"></a> | <a href="https://pepy.tech/projects/gnougo-flow-core"><img src="https://static.pepy.tech/badge/gnougo-flow-core" alt="gnougo-flow-core downloads"></a> | Python port of the YAML workflow DSL engine. See [`librairies/python/gnougo-flow-core/README.md`](librairies/python/gnougo-flow-core/README.md). |

---
