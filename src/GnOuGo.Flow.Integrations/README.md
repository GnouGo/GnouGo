# GnOuGo.Flow.Integrations

`GnOuGo.Flow.Integrations` supplies the concrete AI provider and MCP transport
implementations used with the provider-neutral `GnOuGo.Flow.Core` contracts.

## Build and test

```powershell
dotnet build src/GnOuGo.Flow.Integrations/GnOuGo.Flow.Integrations.csproj
dotnet test tests/GnOuGo.Flow.Integrations.Tests/GnOuGo.Flow.Integrations.Tests.csproj
```

## Usage

Create a `RoutingLLMClientAdapter` from a `GnOuGo.AI.Core.RoutingLLMClient` and
a `ConfiguredMcpClientFactory` from the host-owned MCP settings. Inject them
through `WorkflowEngine.LLMClient` and `WorkflowEngine.McpClientFactory`.
For cost telemetry, also assign a `ModelMetadataUsageCostEstimator` to
`WorkflowEngine.ModelUsageCostEstimator`. Pass the effective `LLMOptions` snapshot to
the estimator when host-configured model pricing overrides must participate in telemetry
or an enforced `LLMUsageBudgetScope`.

Currency-aware limits use `ModelMetadataUsageCostEstimator.EstimateCostWithCurrency`
and `EcbExchangeRateProvider`. The exchange provider first applies fresh static operator
quotes, including their inverse, then fetches the official ECB daily reference-rate XML
over HTTPS and derives cross-rates through EUR. Configure its `HttpClient` timeout in the
host (Agent.Server uses ten seconds). Quotes older than the configured maximum age are
rejected; the default is seven days. Requests contain only the configured ECB URL—never
provider, model, prompt, tenant, or credential data. Network, parsing, stale-rate, and
unsupported-currency failures return no quote so the Flow.Core budget fails closed.

The integration package owns provider and transport mappings. Flow.Core never
references this package or another GnOuGo component.

MCP discovery maps the standard protocol `ReturnJsonSchema` to Flow.Core's compatible
`McpToolInfo.OutputSchema` and immediately resolves its provider-neutral output-contract
provenance. Valid protocol-declared schemas are authoritative; invalid schemas carry validation
errors and example/description-derived shapes remain advisory hints. Servers should therefore
publish `ReturnJsonSchema` for every structured result that downstream workflows need to
dereference.

`RoutingLLMClientAdapter` maps AI.Core's redacted `LLMProviderException` and every
`LLMProviderFailureKind` to Flow.Core's independent `LLMClientException` and
`LLMClientFailureKind`. It preserves retryability, HTTP status, and safe provider code
without copying raw provider response bodies.

Host adapters can reuse `RoutingLLMClientAdapter.MapRequest` and `MapResponse` to preserve output ceilings, disabled transport retries, completion status, tool calls, and usage consistently.

## Durable planning runtime

Register `TypedWorkflowPlanner` as `WorkflowEngine.WorkflowPlanner` and
`Planning.WorkflowPlanningRuntimeFactory.CreateWorkspace()` as `PlanningRuntimeFactory`.
The factory opens an exclusive tenant/session lease and stores schema-5 snapshots, immutable
requests, completed receipts, and cumulative budgets through the public KeyVault record API.
`GnOuGo.Flow.Planning` remains independently publishable with only Flow.Core as a dependency.
`RoutingLLMClientAdapter` exposes declared reasoning capabilities. Planning verifies
the phase profile before dispatch and persists it with the exact request. Requests
target 80% of the configured input ceiling; unknown usage remains nullable.

Session identity includes the run and call site. Reopening an unchanged run reuses completed
receipts; a reserved dispatch without a receipt stops. Changing the initial request under the
same run ID is a conflict. Lease files contain no content and live under the workspace-resolved
`.GnOuGo/data/flow-planning-v5/leases` directory. All payloads use `flow-planning-*-v5` encrypted
record namespaces. Agent.Server's designer retains its EF-backed session indexes.

Planner checkpoints emit the shared `GnOuGo.Flow.Planning` convergence and gate events
through Flow runtime telemetry. Durable request identities deduplicate hole exposures,
and trace events carry tenant and hole identities without adding hole IDs to metrics.
