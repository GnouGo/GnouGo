
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

## 📦 Install gnougo

Windows:

```powershell
winget install GnouGo
```

macOS:

```bash
brew tap GnouGo/tap
brew install --cask gnougo
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

---

## 🔌 MCP Servers

| Server | Transport | Description |
|---|---|---|
| **GnOuGo.Agent.Mcp** | HTTP | Agent definitions, chat history, and user configuration (default LLM, default agent). |
| **GnOuGo.KeyVault.Mcp** | HTTP | Encrypted secret storage: tenant management, create/read/update/delete secrets. |
| **GnOuGo.Browser.Mcp** | stdio | Web navigation via Playwright: open pages, click, fill forms, screenshot, and more. |
| **GnOuGo.Cmd.Mcp** | stdio | Safe shell command execution through a strict allowlist (deny-by-default, cross-platform). |
| **GnOuGo.Code.Mcp** | stdio | Local code operations: read files, search text, summarize a project, and suggest changes via GitHub Copilot. |
| **GnOuGo.Document.Mcp** | stdio | Read and write office/text documents (PDF, DOCX, XLSX, PPTX, Markdown, CSV, JSON…). |
| **GnOuGo.DocIngestor.Mcp** | HTTP | Document ingestion pipeline: download, chunk, embed into a vector store, and semantic search. |

---

## 📚 Published Flow Libraries

The Flow DSL engine is available as two user-facing libraries:

| Library | Package | README | Usage |
|---|---|---|---|
| **GnOuGo.Flow.Core** (.NET) | [NuGet: `GnOuGo.Flow.Core`](https://www.nuget.org/packages/GnOuGo.Flow.Core) | [`src/GnOuGo.Flow.Core/README.md`](src/GnOuGo.Flow.Core/README.md) | NativeAOT-compatible .NET workflow DSL engine for applications, CLIs, APIs, and agents. |
| **gnougo-flow-core** (Python) | [PyPI: `gnougo-flow-core`](https://pypi.org/project/gnougo-flow-core/) | [`librairies/python/gnougo-flow-core/README.md`](librairies/python/gnougo-flow-core/README.md) | Python 3.10+ port with the same YAML DSL concepts and the `gnougo-flow` CLI. |

---
