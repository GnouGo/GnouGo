# Dependency refresh (September 2026)

Issue #103 audits all 57 direct NuGet packages, 21 frontend/build npm packages,
and 14 external Python dependency requirements against their official registries.
42 NuGet packages and 14 npm packages had newer stable releases; the remaining
direct packages were already current. Both Python manifests and universal lockfiles
were refreshed, including development and build requirements. Framework targets
remain .NET 10 and Python 3.10+; prerelease upgrades are not requested.

| Area | Updated versions |
| --- | --- |
| Microsoft runtime extensions, EF Core and SQLite | 10.0.12 |
| OpenTelemetry .NET | 1.19.1; HTTP/ASP.NET instrumentation 1.19.0 |
| gRPC / Protobuf .NET | 2.84.0 / 3.36.2 |
| Copilot SDK / bundled CLI | 1.0.14 / 1.0.88 |
| PDF / rendering / ONNX | PdfPig 0.1.16, rendering 0.1.16.4, SkiaSharp 4.152.1, ONNX 1.30.0 |
| .NET tests | Test SDK 18.10.1, xUnit v3 framework 4.0.1, VS adapter 4.0.0, bUnit 2.11.3 |
| React / types | 19.3.0 |
| Frontend build | Vite 8.3.0, React plugin 6.1.1, Sass 1.105.0 |
| Diagrams / YAML / flow editor | Mermaid 12.0.0, js-yaml 5.4.2, XYFlow 12.11.6 |
| Python integrations | OpenAI 3.18.0, MCP 2.2.0, OpenTelemetry 1.44.0 |
| Python tooling | pytest 9.1.1, pytest-asyncio 1.4.0, Ruff 0.16.8, Hatchling 1.32.4 |

## Compatibility decisions

- Use `xunit.v3.mtp-off` 4.0.1 with the updated Visual Studio adapter to preserve
  existing VSTest commands and IDE integration. The default xUnit package now uses
  Microsoft Testing Platform v2, which rejects those .NET 10 VSTest commands.
  The AI and Agent test assemblies use the new parallelization attribute while
  preserving their serial execution requirements. See the [xUnit release notes](https://xunit.net/releases/v3/4.0.0).
- MCP 2 retains the client session lifecycle but uses snake_case Python attributes.
  Explicit mappings preserve tool schemas, resource MIME types, tool errors and
  camelCase content payloads. A real subprocess SDK test checks this boundary.
  See the [SDK migration guide](https://github.com/modelcontextprotocol/python-sdk/blob/main/docs/migration.md).
- Python 3.10 has distinct built-in and `asyncio` timeout exception classes.
  MCP and LLM error mapping accepts both, preserving timeout/cancellation semantics
  on every supported Python version. The oldest-version CI check caught this gap.
- Mermaid 12 requires Node.js 22.12+ and ES2024 browsers (Safari/WebKit 17.4+).
  Explicit Dagre/classic settings preserve the existing diagram presentation.
  Its indivisible ELK module has a narrowly scoped size budget; unrelated chunks
  still fail the build when they exceed the existing budget.
  See the [Mermaid release notes](https://github.com/mermaid-js/mermaid/releases/tag/mermaid%4012.0.0).
- EF-backed components retain EF Core/SQLite and their compiled-model/AOT checks.
  Workstation settings, endpoints and credentials remain ignored and unpublished.
- The Mermaid/Chevrotain dependency tree pins an older `lodash-es`; override it
  to 4.18.1 to address [template code injection](https://github.com/advisories/GHSA-r5fr-rjxr-66jc)
  and [prototype pollution](https://github.com/advisories/GHSA-f23m-r3pf-42rh).
  Re-run `pnpm audit` after lockfile changes.
- GitHub Actions are pinned to current stable releases, including the Node.js 24
  checkout/setup/cache/artifact and Docker actions. The latest upstream tag action
  (6.2) still declares Node.js 20; GitHub runs it on Node.js 24. No deprecated-runtime
  opt-out or warning suppression is added. Release conditions and permissions are
  unchanged.

## Reproduce validation

```sh
corepack pnpm install --frozen-lockfile --strict-peer-dependencies
corepack pnpm --recursive --workspace-concurrency=2 run build
corepack pnpm --recursive --if-present test
dotnet restore GnOuGo.Agent.sln --disable-parallel -m:1 -p:SkipClientBuild=true -warnaserror
dotnet build GnOuGo.Agent.sln --no-restore -m:1 -p:SkipClientBuild=true -warnaserror
dotnet test GnOuGo.Agent.sln --no-build --no-restore -m:1 -p:SkipClientBuild=true -warnaserror
```

In each Python package directory, run `uv run --locked --extra dev ruff check .`,
`uv run --locked --extra dev pytest -q`, and `uv build`. The dependency workflow
checks Python 3.10 and 3.14; the existing stable test workflow checks Python 3.12.
GitHub Actions also exercise Linux server/Docker builds, desktop packages,
KeyVault/OTLP publishes, planning AOT, and all six proxy Native AOT targets.
Release publication stays restricted to the existing main-branch release policy.
