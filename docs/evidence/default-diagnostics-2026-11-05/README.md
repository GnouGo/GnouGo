# Default local diagnostics and effective models — 2026-11-05

Implementation commits: `b4eaf357` (defaults and model resolution) and `51fed8a2`
(interactive designer readiness). The existing solution and `.vscode/` edits were
preserved. Nothing was merged or released.

Start normally with `dotnet run --project src/GnOuGo.Agent.Server`. Local startup
opens the protected operator browser. New authenticated Designer and chat plans
default to diagnostic warnings; Strict remains selectable. Ordinary unauthenticated
planning remains strict. The Diagnostic launch profile remains a compatible alias.
See [activation, settings and mandatory controls](../../diagnostic-planning.md).

## Model selection

The running user's server was inspected read-only before implementation and already
reported `OpenAi / gpt-6.1-sol-2026-09-29`. Its older GPT-5.5 startup assignment could
disagree with effective workflow settings because startup applied persisted user
defaults after KeyVault hydration.

The new resolver reads user preferences in process before workers/browser startup.
For the selected provider, its configured KeyVault model wins; persisted user model
and host defaults are fallbacks. Startup, chat, new workflow runtimes and provider
API reads use that resolver. Logs identify the effective model and its source;
`/api/llm/providers` exposes `defaultModelSource`. Resolving defaults does not rewrite
persisted preferences. Historical planning captures and prices remain unchanged.

The dated Sol identifier in this evidence is an explicit private deployment override,
not a newly invented public model or catalog alias.

## Controlled-provider browser evidence

These are **controlled-provider results**, without paid inference or workflow
execution. The test starts the actual Agent.Server host and embedded collector with
the normal default diagnostic/trace settings. It replaces the operating-system
browser launcher with a recorder, then opens that same one-use bootstrap URL in
headless Playwright. The HTTP provider and MCP catalog are controlled fixtures.

The fixture seeds a stale GPT-5.5 user default and a configured Sol 6.1 deployment.
It checks the resolved model before startup, after startup and after restart, and
checks every physical HTTP request uses the configured deployment. Designer and
chat select warnings without an explicit mode change; Strict is selectable. An
authenticated API request omitting threshold mode captures diagnostic authority,
while unauthenticated/remote callers acquire none.

- [Active diagnostic phase](controlled-active-phase.png).
- [Threshold warning permitting continuation](controlled-warning-continues.png).
- [Expanded input in the OTel Pipeline / LLM calls panel](controlled-otel-input.png).
  The browser also verifies the response document is available in the same panel.
- [Expanded serialized HTTP exchange](controlled-llm-exchange.png).
- [Redacted exchange export](controlled-call-redacted.json) and
  [controlled session identities](controlled-browser-result.json).

The same scenario covers retries, invalid/empty/partial responses, correction,
cancellation, reload, encrypted-store restart, pagination/search/export, redaction,
antiforgery and foreign-key rejection. Span assertions retain model/usage/content
references while excluding the business prompt and fixture credential from exported
LLM attributes. Detailed content remains encrypted and protected.

A full-run attempt exposed a readiness race in the browser helper's fixed delay.
The final designer exposes its actual interactive state and prevents submission
before connection; the verifier waits for that state without resubmitting requests.
The superseded run is not presented as a passing qualification.

## Validation and limits

[Exact commands and results](validation.json), [source hashes](source-manifest.json)
and [package/binary hashes](publication-results.json) bind the qualification to the
implementation. The completed full solution run passed **7,765 tests**, with **0 failures**
and **12 skips** across 33 assemblies (7,777 total). The skips are seven Windows-native
PowerShell cases and five opt-in live Copilot/GitHub cases.
The solution build treats warnings as errors. The frontend production build and
published encrypted-storage smoke are recorded separately.

The server publish retains the existing partial-trim boundary and excludes
rebundling unchanged MCP tools. Only the two existing EF Core Tasks experimental
notices (query precompilation and NativeAOT support in Files.Server) are accepted;
no new warning suppression is introduced. Windows-native and opt-in live-provider
checks are reported explicitly. This change does not alter the planner/core Native
AOT inputs; no new Native AOT qualification is claimed here.

No live inference was dispatched. The retained provider incident is inspected only
with its frozen read-only inspector; its before/after comparison is recorded in
[the incident summary](retained-incident.json). That old binary is not used to
qualify the new implementation. Existing uncertainty, reservations and financial
ceilings are not bypassed. Earlier live-readiness limitations remain documented in
[the original diagnostic report](../diagnostic-planning-2026-11-05/README.md).
