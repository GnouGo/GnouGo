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

`RoutingLLMClientAdapter` maps AI.Core's redacted `LLMProviderException` and every
`LLMProviderFailureKind` to Flow.Core's independent `LLMClientException` and
`LLMClientFailureKind`. It preserves retryability, HTTP status, and safe provider code
without copying raw provider response bodies.
