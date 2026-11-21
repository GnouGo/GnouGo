# GnOuGo.Flow.Copilot

Independently publishable `IAgentTaskRunner` adapter for schema-9 `agent.run` stages.
It uses an injected `IMcpClientFactory`; the configured GithubCopilot MCP server uses
its existing managed session APIs. This package contains no SDK execution loop.

```csharp
engine.AgentTaskRunners["coding"] = new CopilotAgentTaskRunner(mcpFactory, configuredServerName);
```

A Server operator can bind several existing MCP tools to a runner. For example:

```json
{
  "Flow": {
    "CopilotRunners": { "research": "GnOuGo.GithubCopilot.Mcp" },
    "CopilotTools": {
      "research": { "catalogue": ["search"], "documents": ["read"] }
    }
  }
}
```

A runner with explicit tool bindings exposes only that allowlist. A separately registered project runner retains its project and command permissions. Discovery reads the configured servers' current tool catalogs. Each selectable
capability identifies the server, method, schemas, effect and metadata by digest.
The approved task selects individual capabilities, declares a finite action count
and supplies verification conditions. A changed contract requires fresh approval.
The SDK receives only the tools selected for the task. Each host call goes through
the workflow's MCP factory, permission checks and tenant/run/invocation context.
The invocation-specific exchange binds each call to its original SDK admission.
Reads can refresh observations. Repeated identical reads and repeated writes are
bounded by the action journal. A cancelled queued call is discarded. A claimed
call whose outcome is unknown remains subject to reconciliation.

For an MCP observation, use verification kind `mcp.result` and the selected
capability ID as the subject. Its evidence contains the actual `request` and
`result`. The workflow adapter checks the terminal receipt against the responses
it observed. Successful host responses are recorded in the encrypted action
journal before control returns to the SDK, so inspection can recover verified
observations even if the final turn is lost. A completed invocation can return its stored receipt without calling
a tool again.

Discover the runner contract before approval. Task permissions, output schema,
verification requirements and cumulative ceilings belong in the approved graph.
The server preserves interactive permissions and controlled project file access.
Commands require an available mandatory Copilot sandbox (`enabled` and managed
`failIfUnavailable`); optional sandbox fallback is rejected. Commands have no network
or credential grants. Existing host filesystem policy can further restrict access.
The adapter activates device-managed policy without changing the configured client
authentication or BYOK provider. Policy discovery and a successful mandatory
enforcement probe are required before dispatch.

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
