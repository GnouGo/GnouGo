# GnOuGo.Flow.Server

ASP.NET Core host and workflow editor for Flow. Execution engines inject the same
Planner v2 as the CLI and Agent.Server. After business behavior review, `workflow.plan`
freezes a deterministic executable skeleton, resolves known bindings and contracts, and
asks the model to assign only unresolved typed fields. Invalid assignments remain staged
for exact-field repair. Only deterministic lowering emits YAML. Compilation, semantic
and scenario validation and final approval are required.
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
budgets. Independent workflow concurrency defaults to 4. Each workflow and validation
gate has five repair attempts by default (`max_repairs_per_workflow_gate`, range 0–10),
within the global call, token, cost and active-time budgets.

Build and run the standalone container from the repository root:

```sh
docker build -t gnougo-flow -f src/GnOuGo.Flow.Server/Dockerfile .
docker run --rm -p 5300:5300 gnougo-flow
curl --fail http://localhost:5300/health
```

The image includes the planner and encrypted persistence dependencies. Its `URLS`
setting binds the exposed port on all container interfaces.

The planning runtime stores encrypted schema-4 sessions and request receipts under the run ID.
Reopening a planning call with that identity reuses completed requests and retained budgets.
Workflow execution checkpoint storage remains a separate host service.

Run requests accept an optional `runId`; responses expose `X-Workflow-Run-Id`.
After restart, resubmit the original workflow and inputs with that ID to reopen its
planning session. This recovers planning state, while ordinary execution steps follow
the submitted workflow. The configured OpenTelemetry tenant owns the persisted session.
