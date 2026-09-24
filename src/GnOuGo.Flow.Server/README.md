# GnOuGo.Flow.Server

ASP.NET Core host and workflow editor for Flow. Engines inject the same semantic and grounded planner as the CLI and Agent.Server. Business planning precedes complete catalog grounding. Deterministic contract validation, mechanical graph construction and isolated scenarios precede final approval. The compiler owns YAML generation and execution verifies the stored approval. Human input uses the server's endpoints. See [architecture](../../docs/workflow-planning-v2.md).

```sh
dotnet build src/GnOuGo.Flow.Server
dotnet run --project src/GnOuGo.Flow.Server
cd src/GnOuGo.Flow.Server/ClientApp
corepack pnpm install --frozen-lockfile
corepack pnpm build
```

The editor exposes semantic planning, model configuration, explicit host policy and budgets. Defaults are eight total model calls and two atomic replanning attempts. Session and workflow execution concurrency are independent of planning.

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
Workflow execution checkpoint storage remains a separate host service.

Run requests accept an optional `runId`; responses expose `X-Workflow-Run-Id`.
After restart, resubmit the original workflow and inputs with that ID to reopen its
planning session. This recovers planning state, while ordinary execution steps follow
the submitted workflow. The configured OpenTelemetry tenant owns the persisted session.
