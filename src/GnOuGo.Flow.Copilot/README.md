# GnOuGo.Flow.Copilot

Independently publishable `IAgentTaskRunner` adapter for schema-9 `agent.run` stages.
It uses an injected `IMcpClientFactory`; the configured GithubCopilot MCP server uses
its existing managed session APIs. This package contains no SDK execution loop.

```csharp
engine.AgentTaskRunners["coding"] = new CopilotAgentTaskRunner(mcpFactory, configuredServerName);
```

Discover the runner contract before approval. Task permissions, output schema,
verification requirements and cumulative ceilings belong in the approved graph.
The server preserves interactive permissions and controlled project file access.
Commands require an available mandatory Copilot sandbox (`enabled` and managed
`failIfUnavailable`); optional sandbox fallback is rejected. Commands have no network
or credential grants. Existing host filesystem policy can further restrict access.

Inference uses explicit output ceilings and non-refundable conservative token
reservations. `usage.metering=reserved_upper_bound` identifies charged allowances,
not observed token counts. Unsupported inference transports fail closed; configured
inference proxies remain in the request path.

Completion claims are not evidence. `command.exit` evidence identifies the exact
observed command and structured exit code. `file.content` evidence identifies an
observed project file and its SHA-256 digest. Interrupted tasks are inspected by
invocation identity; uncertain effects are never redispatched and a crashed process's
internal Copilot session is not automatically restored.

```sh
dotnet build src/GnOuGo.Flow.Copilot
dotnet test tests/GnOuGo.Flow.Copilot.Tests
dotnet pack src/GnOuGo.Flow.Copilot -c Release
```

See [bounded-coding.yaml](../GnOuGo.Flow.Cli/examples/bounded-coding.yaml) for a
mixed deterministic/agent workflow with exact command and changed-file evidence.
The CLI validates the YAML without dispatching the agent:

```sh
gnougo-flow validate src/GnOuGo.Flow.Cli/examples/bounded-coding.yaml
```

Mandatory sandbox policy is documented in [GitHub's managed settings reference](https://docs.github.com/en/copilot/reference/enterprise-administrators/enterprise-managed-settings#sandbox).
The adapter checks enforcement before dispatch and does not modify global managed
policy. Preparation failures and task failures remain inspectable terminal receipts;
transport failures remain uncertain until reconciliation.
