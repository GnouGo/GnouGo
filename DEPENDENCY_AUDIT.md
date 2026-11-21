# Dependency audit exceptions

## 21 November 2026 · .NET 11

The 77 solution projects target `net11.0` and C# 15. Building uses SDK 11.0.100.
Framework-dependent deployments and library consumers require the .NET 11 runtime.
The refresh checked the 59 existing direct package IDs against the stable NuGet
indexes, removed references supplied by the new framework, and regenerated the five
compiled SQLite models with EF Core 11.0.0. CI uses the pinned SDK. Linux SDK and
runtime image tags are pinned to 11.0.100 and 11.0.0. The solution-wide NuGet
checks for outdated packages and known vulnerabilities (including transitive
dependencies) returned zero findings in 77 projects.

The managed client uses GitHub.Copilot.SDK 1.0.34, which supports protocol 3.
A published MCP handshake against stable runtimes 1.0.114 and 1.0.113 found
protocol 4. Runtime 1.0.112 returned version 1.0.112, protocol 3 and a connected
session with zero inference calls. Both projects pin this compatible version.
The arm64 release archive matched GitHub's SHA-256
`3c924fb18d6b309adc21b592ff32c55916d43448df4107d37e5a9ad0daf9478f`.
[Copilot runtime release](https://github.com/github/copilot-cli/releases/tag/v1.0.112)

On macOS the native runtime package ships unavailable Swift debug-module paths in
the Apple crypto archive, and duplicate GC debug object names and timestamps. The
Release AOT target prepares publish-local copies. GC objects whose name and
timestamp collide receive unique archive member names of equal length, preserving code, debug
contents and symbol offsets. Python 3 is used by this Darwin publish target.
The native planner, CLI, Flow Server and encrypted Copilot recovery smoke tests
passed on macOS arm64. The Intel macOS .NET 11 GSSAPI object contains an
unaligned pointer. Xcode's current linker with classic relocations produces an
executable and reports two alignment warnings from that object. The locally
cross-compiled Intel release archive passed its extracted-binary HTTP,
streaming, provider and configuration checks.

The Browser MCP package includes the Chromium, headless shell and FFmpeg
revisions selected by its pinned Playwright installer. A Desktop publish
timed out fetching an already installed headless shell from the CDN. The
publish target now stages complete, matching installations from the build
cache and runs Playwright's normal installer against the release directory.
A disconnected check matched the SHA-256 of all three executables and ran
Chrome for Testing 159.0.8154.6 from that directory.

SDK 11.0.100 exports its development hot-reload module from referenced web
applications. This broke the Agent Server Debug build with four unmatched module
endpoints. The scoped build target exports the referenced applications' ordinary
assets and keeps each application's own debug module. A two-host reproduction
and the Agent Server Debug build passed with zero warnings. Remove the target
when updating to an SDK containing the upstream fix.
[.NET SDK fix](https://github.com/dotnet/sdk/pull/57196)

EF Core 11 uses a Roslyn design-time process when precompiling SQLite queries.
Its JSON protocol requires reflection. The Files and Flow persistence projects
use the framework-dependent tool build for this step, including when the caller
publishes a trimmed single file. The two index entities in Agent Server allow
the generated conditional service cast. Captured tenant keys and cancellation
tokens use local variables throughout the precompiled query. Generated query
files with nullable annotations receive an explicit annotation context. Agent
Server's interactive Blazor release retains its existing partial-trim profile.
The published binary verified encrypted planning recovery, the health endpoint,
the root page and Blazor negotiation.

The host single-file bundle leaves LLamaSharp native libraries and referenced
executables' runtime configurations beside the executable. The pinned ASP.NET
Core Endpoints assembly embeds a second copy of the Forms hot-reload substitution.
The managed release reports IL2101 for this resource. Both the signed assembly
identity and the foreign resource target are checked by the publish validator.
The macOS arm64 host audit recorded 108 distinct warning origins after switching
the planning HTTP responses and trace JSON arrays to typed serialization.
[Endpoints resource](https://github.com/dotnet/aspnetcore/blob/v11.0.0/src/Components/Endpoints/src/Properties/ILLink.Substitutions.xml) ·
[Forms resource](https://github.com/dotnet/aspnetcore/blob/v11.0.0/src/Components/Forms/src/Properties/ILLink.Substitutions.xml)

## Previous audit of 7 November 2026

The frontend exceptions below were reviewed on 2 August 2026.

All 59 direct NuGet package IDs, including conditional Linux native references, were checked against the official NuGet registry. The refresh updates 51 IDs across 238 references in 63 projects to their latest stable releases. The .NET 10 target and pinned SDK policy remain unchanged. NuGet reports no deprecated or vulnerable packages, including transitive dependencies. Detailed versions and validation are recorded in [the .NET refresh report](docs/evidence/dotnet-dependency-update-2026-11-07/README.md).

LLamaSharp and its CPU backend remain version-aligned, including native asset imports for package consumers. The Copilot SDK update uses its matching pinned CLI runtime, with the existing capability/permission allowlist. Existing encrypted records, EF compiled models and database schemas are retained; published persistence checks verify the new EF/SQLite runtime against them.

The frontend dependencies were not changed or re-audited by this .NET-only refresh. The previously recorded `pnpm audit` exceptions are:

| Advisory | Resolved dependency | Exposure in GnOuGo | Disposition |
|---|---|---|---|
| [GHSA-g7r4-m6w7-qqqr](https://github.com/evanw/esbuild/security/advisories/GHSA-g7r4-m6w7-qqqr) (low) | `vite@8.2.0` → `esbuild@0.27.7` | The issue affects the esbuild development server on Windows. Production bundles do not run that server, and GnOuGo's development servers retain their loopback-only default. | Keep the latest compatible Vite release; update when Vite accepts esbuild `0.28.1` or later. Do not override Vite's transitive range. |
| [GHSA-qwww-vcr4-c8h2](https://github.com/remix-run/react-router/security/advisories/GHSA-qwww-vcr4-c8h2) (high) | `react-router-dom@7.18.2` → `react-router@7.18.2` | The advisory applies only to unstable React Server Components APIs. The OTLP Collector frontend uses declarative `BrowserRouter`, `Routes`, `Route`, `Link`, and `useLocation` APIs and does not enable RSC mode. | Keep the latest compatible React Router DOM release; move to a patched release when one is available without forcing an incompatible transitive major. |

Re-run these checks during every dependency refresh:

```bash
dotnet package list --project GnOuGo.Agent.sln --outdated
dotnet package list --project GnOuGo.Agent.sln --deprecated
dotnet package list --project GnOuGo.Agent.sln --vulnerable --include-transitive
corepack pnpm outdated --recursive
corepack pnpm audit
```
