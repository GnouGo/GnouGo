# Planning trace inspection validation — 2026-09-20

Validated feature revision: `2a4e27a`, including backend lookup commit `facb9b6`.
Starting revision: `e1aea98`. Planner behavior, schema-7 storage and public planning
REST contracts are unchanged. This is an observability/UI validation, not another
live-model evaluation or a correction to the stopped planner's diagnostics.

## Delivered behavior

The workflow designer lists Designer and Chat planning sessions from their original
tenant-scoped stores. Both the list and detail header have a **Traces** button.
Chat-created sessions use `?source=workflow` and remain read-only. The shared chat
and planning panel displays spans, events, recorded model usage and logs. Multiple
planning traces have a timestamped selector and retain separate identities.

Lookup verifies exact session attributes and tenant ownership. Local capture
includes designer activity before the first chat opens; persisted collector data
supports inspection after restart. Closing the panel or navigating cancels its
refresh subscription. No planner command or model dispatch occurs when inspecting.

## Automated validation

All commands ran in Release on macOS ARM64 with .NET SDK 10.0.300.

| Command | Result |
| --- | --- |
| `dotnet test tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj -c Release -m:1 --no-restore` | 345 passed, 0 skipped |
| `dotnet test tests/GnOuGo.OtlpCollector.Server.Tests/GnOuGo.OtlpCollector.Server.Tests.csproj -c Release -m:1 --no-restore` | 7 passed, 0 skipped |
| `corepack pnpm build` in `src/GnOuGo.Agent.Server/ClientApp` | Passed without warnings |
| `dotnet build src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj -c Release -m:1 --no-restore -warnaserror` | 0 warnings, 0 errors |
| `dotnet build src/GnOuGo.Agent.Desktop/GnOuGo.Agent.Desktop.csproj -c Release -m:1 --no-restore -warnaserror` | 0 warnings, 0 errors |
| `git diff --check` | Passed |

Coverage includes identical session identifiers in different origins, foreign
tenants, malformed stored ownership, persisted lookup with an empty local cache,
duplicate telemetry deliveries, trace selection, unavailable telemetry, cancellation,
navigation cleanup, read-only workflow controls and existing chat interactions.

An initial parallel MSBuild invocation hit a shared static-web-assets cache lock.
The successful checks above used `-m:1`. No warning suppressions or weakened tests
were added.

## Retained Desktop failure inspected through the UI

- Session: `b5de7ed0d66113e0ab1d00d4410750f7c9769e860a56eca8b6a541b95c5d7f86`.
- Trace: `2b38d117364eb8b60b86a90e78adae19`.
- The actual Agent.Server Blazor page was exercised in headless Chromium against
  an isolated host with planning background processing disabled. Telemetry used a
  filesystem snapshot of the stopped Desktop collector database; encrypted session
  records were read through public KeyVault APIs.
- The detail page showed Chat origin, stopped status, revision 3, one model call,
  and only the read-only **Traces** action.
- The panel loaded **26 spans and 170 log entries**. Switching to Logs and closing
  the panel worked. No JavaScript exceptions occurred. The planning list also
  contained the session with its source-qualified link and trace button.
- Before/after comparisons matched the payload hashes and update timestamps of
  all four retained records: session, original model request, completion receipt,
  and cumulative budget. Inspection made no inference request, workflow approval,
  execution, or GitHub write.
- The temporary host and browser were stopped. The unrelated existing host on
  port 4317 was left running. This check was browser-based; the Desktop binary was
  built, but no new native Desktop planning/execution run is claimed.

## Limits

Trace retention is independent of encrypted planning receipts. Expired or absent
telemetry cannot be reconstructed by the viewer. Collector lookup is bounded to
500 candidates and displays a notice when reached. Existing usage summaries sum recorded attributes and are not complete billing
evidence; absent telemetry cannot establish zero usage. The previously identified diagnostic-to-intent repair issue is still a
separate task; viewing its trace does not make the failed proposal executable.
