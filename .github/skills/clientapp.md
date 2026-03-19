# ClientApp Skill — GnOuGo.Agent

## Target Stack
- **Front-end (per API)**: React + TypeScript + Vite + SCSS (BEM) + pnpm
  - The front-end lives **in `ClientApp/`** within the **API** project
  - The Vite build outputs assets into **`wwwroot/`** (at build/publish time)

---

## Front-end (within APIs)
- The React front-end lives in `ClientApp/`.
- Build pipeline:
  - `ClientApp` is built by Vite
  - generated files are copied into `wwwroot/`
- The API serves static files from `wwwroot/`.

> Note: **Blazor is only used for GnOuGo.Agent (final tool)**.  
> APIs and libraries remain independent of Blazor.
