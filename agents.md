# agent.md — GnOuGo.Agent (cross-cutting rules)

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

---

## Recommended Technical Structure (mono-repo)
- `src/GnOuGo.<LibName>/` : library
- `src/GnOuGo.<LibName>.Api/` : API (if exposed)
  - `ClientApp/` : React/Vite front-end
  - `wwwroot/` : Vite build output (static assets)
- `src/GnOuGo.<LibName>.Cli/` : CLI (if needed)
- - `src/GnOuGo.<LibName>.Mcp/` : MCP stdio and/or http (if needed)
- `src/GnOuGo.Agent/` : **final tool** (application) in **Blazor**
- `tests/GnOuGo.<LibName>.Tests/` : unit tests

---

## Cross-cutting Technical Rules
- **Multi-tenant**: all persisted entities must carry a `TenantId`.
- **Observability**: **OpenTelemetry** instrumentation (traces/metrics/logs) with `TenantId` propagation.
- **Security**:
  - secrets stored **encrypted** in the database,
  - no sensitive data in plain text at rest.
- **Unit tests**:
  - one test project per library,
  - no mandatory dependency on other libraries for testing.

---

## Expected Deliverables per Component
Depending on the need, a component may provide:
- a **library** (`.dll` + package),
- an **API** (ASP.NET Core),
- a **CLI** (console),
- a **front-end** in `ClientApp/` integrated with the API,
- dedicated **unit tests**.

---

## Final Goal
Assemble components to produce:
- a **local agent** (MVP) based on SQLite,
- a **GnOuGo.Agent** application in **Blazor** targeting **desktop** (Photino.NET),
- with a build pipeline capable of producing a **Native AOT** and/or **maximum Trim** binary,
- without tight coupling between libraries.
- Keep each component's README.md up to date with build, test, and usage instructions.
