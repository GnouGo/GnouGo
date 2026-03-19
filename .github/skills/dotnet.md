# .NET Skill — GnOuGo.Agent

## Target Stack
- **Backend**: .NET 10
  - When a library is needed, verify its availability and only use it if it is compatible with AOT compilation.
- **Database Access**: Entity Framework (EF Core)
- **Final tool UI**: only **GnOuGo.Agent (final application)** uses **Blazor**
  - Goal: **desktop** execution via **Photino.NET**
  - Possible evolution: future migration/porting to another technology for **mobile**

## Build Goal
- **Native AOT** and/or **maximum Trim** (aggressive), as much as possible across all components.

---

## Storage (local MVP)
- Each library/domain uses **SQLite**.
- **Isolated schema**: each library has its **own tables** (no shared tables).
- Database access via **EF Core**:
  - one `DbContext` per library,
  - migrations managed per library.
  - Dates must be stored as UTC timestamps only.

---

## AOT / Trim Constraints (ground rules)
To maximize **AOT** and **Trim** compatibility:

- **Avoid undeclared dynamic reflection**.
- **Avoid**:
  - dynamic assembly loading,
  - uncontrolled serialization (unknown types),
  - DI based on massive scanning/reflection,
  - dynamic proxies that are not AOT-friendly.
- **Prefer**:
  - **explicit code**,
  - **source generators** when available,
  - **System.Text.Json** serialization with known types,
  - EF Core options compatible with AOT/trim (stable model, no exotic runtime model building).
  - EF Core with compiled queries.
