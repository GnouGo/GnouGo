# Dependency audit exceptions

NuGet last reviewed: 2026-11-07. Frontend exceptions below were last reviewed on 2026-08-02.

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
