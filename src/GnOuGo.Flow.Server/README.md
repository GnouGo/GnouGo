# GnOuGo.Flow.Server

ASP.NET Core host and workflow editor for Flow. Engines inject the same hybrid planner as the CLI and Agent.Server: requirements, progressive discovery, one executable graph, deterministic validation and approval. Simulated validation is separate from observed execution evidence. The compiler owns YAML generation and execution verifies the stored approval. Human input uses the server's endpoints. See [architecture](../../docs/workflow-planning-v9.md).

```sh
dotnet build src/GnOuGo.Flow.Server
dotnet run --project src/GnOuGo.Flow.Server
cd src/GnOuGo.Flow.Server/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm build
```

The editor exposes workflow stages, model configuration, explicit host policy and budgets. Defaults are eight total model calls and two atomic replanning attempts. Session and workflow execution concurrency are independent of planning.

Build and run the standalone container from the repository root:

```sh
docker build -t gnougo-flow -f src/GnOuGo.Flow.Server/Dockerfile .
docker run --rm -p 5300:5300 gnougo-flow
curl --fail http://localhost:5300/health
```

The image includes the planner and encrypted persistence dependencies. Its `URLS`
setting binds the exposed port on all container interfaces.

The planning runtime stores encrypted schema-9 sessions and request receipts under the run ID.
Reopening a planning call with that identity reuses completed requests and retained budgets.
Execution uses `GnOuGo.Flow.Persistence`: encrypted KeyVault journal payloads and a rebuildable EF Core/SQLite index.

The configured host tenant owns `/api/tenants/{tenantId}/runs`. `GET` lists runs; `GET /{runId}` inspects inputs, receipts, evidence, budgets and recovery state. `POST /{runId}/resume`, `/cancel`, and `/reconcile` require `{ "expectedRevision": N }`. Reconciliation additionally accepts `invocationId`; omitting `confirmedStoppedReason` asks the agent adapter to observe the outcome. An explicit reason confirms a stopped operation as failed, never as successful. These replace the old checkpoint resume route.

`POST /{runId}/human-input` accepts `{ "expectedRevision": N, "invocationId": "...", "response": ... }` and persists the answer before acknowledging it. Invocation paths distinguish nested calls, branches and loop iterations. Unknown effects block cleanup and require reconciliation; managed Copilot sessions are not automatically restored after a process crash.

Run requests accept an optional `runId`; responses expose `X-Workflow-Run-Id`.
After restart, inspect the run and use its revision-checked resume endpoint. Initial execution rejects an existing run ID. Responses also expose `X-Workflow-Tenant-Id`; the configured tenant owns the journal.

Persistence paths can be configured with `KeyVault:DatabasePath`, `Flow:Execution:IndexPath`, `Flow:Execution:OwnerPath` and `Flow:Planning:OwnerPath`. Omitted paths use workspace helpers. Hosts sharing a KeyVault must share the execution owner directory.
