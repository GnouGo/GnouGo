---
name: gnougo-frontend
description: Implement or review GnOuGo ClientApp, TypeScript, CSS/SCSS and Razor/Blazor UI changes, including interaction tests, Vite builds and frontend validation.
---

# Frontend

- Follow the component's existing framework, styling and layout. Agent.Server uses Blazor with Vite assets; other ClientApps may use React. Do not introduce a second UI stack.
- Edit source in `ClientApp` or the owning Razor components. Let its Vite configuration determine output paths; never hand-edit generated `wwwroot` assets. Follow the component's policy for committing build output.
- Use the Node/pnpm versions in root `package.json` and CI, the shared `pnpm-workspace.yaml` and root lockfile. Install with `pnpm install --frozen-lockfile --strict-peer-dependencies`; avoid separate npm/yarn lockfiles.
- Reuse typed API contracts and existing components/styles. Preserve server-side policy enforcement; client validation does not replace it. Keep loading, error, empty and cancellation states usable, with semantic controls and keyboard access.
- For behavior changes, add focused interaction tests using existing tooling; verify the affected flow and responsive layout. For Razor changes, also run the owning host tests.
- Run the affected package's available test, lint and type-check scripts, then its production build. Use `pnpm --dir src/GnOuGo.<Name>.Server/ClientApp run build` for a single frontend.
- For shared frontend/dependency changes, match CI: `pnpm --recursive --workspace-concurrency=2 run build` and `pnpm --recursive --if-present test`. Missing scripts or untested browser behavior are limitations, not passing checks.
