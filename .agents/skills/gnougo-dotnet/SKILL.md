---
name: gnougo-dotnet
description: Implement or review C#/.NET libraries, APIs, CLIs, MCP servers, persistence, tests and packaging in GnOuGo, including Native AOT and trimming.
---

# .NET

## Architecture and conventions

- Use the SDK in `global.json`. Components live in `src/GnOuGo.<Name>`; dedicated tests in `tests/GnOuGo.<Name>.Tests`. Hosts use `.Server`, not `.Api`; follow existing component conventions.
- Flow.Core must reference no other GnOuGo package/project. Keep provider-neutral interfaces there; inject concrete AI/MCP integrations from consumers.
- Keep EF Core + SQLite in components that own EF persistence. Never replace it with raw SQLite to silence AOT warnings; raw stores remain confined to existing designated owners. Keep migrations/models with their owner.
- Every persisted entity carries `TenantId`; propagate it through OpenTelemetry and use `GnOuGoWorkspace` for paths. Use UTC timestamps.
- Access secrets, encrypted records and MCP/Agent.Server configuration through public KeyVault APIs, never direct SQL. Direct KeyVault readers receive the vault location, not decrypted environment values. KeyVault stays generic; consumers own names, DTOs, serialization and configuration mapping.
- Inspection uses bounded session/key metadata cursors and authenticated ranges through KeyVault's optional reader capability. Preserve replay bytes, atomic chunk publication, ownership/version checks and Unicode/redaction boundaries. Historical inspection may hydrate once on explicit access; polling must not convert old documents or decrypt tenant history. Journals remain authoritative for I/O/recovery/accounting; OTel remains observability. Post-persistence inspection observers cannot change dispatch/admission/replay. Browser fixtures let Kestrel own loopback port zero; never probe and release before binding.
- KeyVault configuration takes precedence: unavailable optional storage permits fallback with a redacted warning; present invalid values fail startup without logging their contents.
- Register `AddGnOuGoToolErrorNormalizer` in MCP servers. Publish truthful schemas/error envelopes; keep stdio progress on stderr and preserve complete result payloads.

## Tests and publishing

- Agent.Server/Desktop application LLM budgets default to explicit tracking-only Unlimited. Persisted local operator preferences require authorization and antiforgery, and apply only to new captured sessions/runs. Keep finite workflow contracts positive and strict. Retain encrypted accounting and uncertain reservations without a ceiling; report missing pricing/FX as unavailable. Never migrate historical budgets or infer new authority during restore. Route Desktop operator bootstrap into Photino using the one-use fragment/cookie flow, with no authority link in logs.

- Prefer explicit registration and source-generated `System.Text.Json`; avoid unbounded reflection, dynamic loading and assembly scanning. Check dependency compatibility with the component's trimming/AOT target.
- Use compiled EF models and precompiled queries where applicable. Any unavoidable framework/dependency suppression must be narrow, publish-local, documented and covered by a published-binary smoke test.
- Reproduce behavior with focused tests in the owning project; keep unit tests package-independent and place cross-component scenarios in integration/host tests.
- Run `dotnet test tests/GnOuGo.<Name>.Tests -warnaserror` for affected components; use `dotnet test GnOuGo.Agent.sln -m:1 -warnaserror` for shared changes.
- For packaging/serialization/AOT changes, run the component's Release pack/publish and existing smoke checks for the affected target. Persistence changes require published encrypted-storage tests. Use the component README and existing CI commands; report skipped or unavailable checks explicitly.
