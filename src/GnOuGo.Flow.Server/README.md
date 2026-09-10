# GnOuGo.Flow.Server

ASP.NET Core host and workflow editor for Flow. Execution engines inject the same
Planner v2 as the CLI and Agent.Server. `workflow.plan` constructs complete typed
subworkflows after business behavior review and emits YAML only through deterministic
lowering. Compilation, semantic and scenario validation and final approval are required.
Human input is routed through the server's human-input endpoints. See
[planning architecture](../../docs/workflow-planning-v2.md).

```sh
dotnet build src/GnOuGo.Flow.Server
dotnet run --project src/GnOuGo.Flow.Server
cd src/GnOuGo.Flow.Server/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm build
```

The editor exposes intent, model configuration, capability constraints, policies and
budgets. Independent workflow concurrency defaults to 4 and typed repairs to 3.

The planning runtime stores encrypted schema-3 sessions and request receipts under the run ID.
Reopening a planning call with that identity reuses completed requests and retained budgets.
Workflow execution checkpoint storage remains a separate host service.

Run requests accept an optional `runId`; responses expose `X-Workflow-Run-Id`.
After restart, resubmit the original workflow and inputs with that ID to reopen its
planning session. This recovers planning state, while ordinary execution steps follow
the submitted workflow. The configured OpenTelemetry tenant owns the persisted session.
