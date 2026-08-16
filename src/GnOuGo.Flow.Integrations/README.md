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
`WorkflowEngine.ModelUsageCostEstimator`.

The integration package owns provider and transport mappings. Flow.Core never
references this package or another GnOuGo component.
