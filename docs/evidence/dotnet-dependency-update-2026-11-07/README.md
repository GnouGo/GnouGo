# .NET dependency refresh — 2026-11-07

All 59 direct NuGet package IDs were checked against the official registry, including conditional platform references. The refresh updates 51 IDs across 238 references in 63 projects; the other eight IDs were already current. [Exact versions](versions.json) retain the old/new values and registry sources. The .NET 10 target and existing SDK policy are unchanged.

| Group | Updated versions |
| --- | --- |
| Microsoft extensions, EF Core, SQLite, ASP.NET testing | 10.0.12 → 10.0.14 |
| MCP packages | 2.2.0 → 2.4.0 |
| GitHub Copilot SDK | 1.0.14 → 1.0.29; matching SDK-pinned CLI 1.0.107 |
| LLamaSharp and CPU backend | 0.27.0 → 0.30.0 |
| Jint | 4.16.3 → 4.17.0 |
| OpenTelemetry SDK/API/exporter | 1.19.1 → 1.20.1 |
| OpenTelemetry instrumentation | 1.19.0 → 1.20.2 |
| SkiaSharp, including Linux native assets | 4.152.1 → 4.155.0 |
| Playwright | 1.62.0 → 1.64.1 |

Additional tokenizer, ONNX, renderer, Markdown, gRPC, CLI and test dependency updates are listed individually in the version manifest. Native backend imports and capability-report versions match the new packages. The existing permission/capability allowlist is preserved; new experimental SDK capabilities are not enabled. Existing database schemas, encrypted records, journal formats and financial authorizations are not migrated by this refresh. Historical qualification evidence remains unchanged.

## Validation

The solution build passed with zero warnings/errors. NuGet vulnerability and deprecation scans include transitive packages and reported no findings. Full tests, published encrypted-storage compatibility and affected Release/AOT checks are in progress; no pending check is claimed passed.

Frontend packages were not changed or re-audited. No paid-provider inference was required for this dependency update.

## Primary references

- [NuGet V3 registry](https://api.nuget.org/v3/index.json).
- [LLamaSharp 0.30 release](https://github.com/SciSharp/LLamaSharp/releases/tag/v0.30.0) and [pinned native iterator](https://github.com/SciSharp/LLamaSharp/blob/v0.30.0/LLama/LLamaStatelessExecutor.cs).
- [Jint 4.17 release](https://github.com/sebastienros/jint/releases/tag/v4.17.0).
- [MCP 2.4 release](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.4.0): OAuth and transport hardening; experimental Skills remain a separate opt-in package.
- [Copilot SDK 1.0.29](https://www.nuget.org/packages/GitHub.Copilot.SDK/1.0.29); its packaged build props pin CLI 1.0.107.
